using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Restore;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;

namespace Orrery.App;

// Remembering the workspace: which tabs were open and where the window was, and bringing them back at the next start.
public partial class MainWindow
{
    static readonly TimeSpan PlacementDelay = TimeSpan.FromMilliseconds(300);
    static readonly ILogger WorkspaceLog = Log.For("Workspace");

    readonly WorkspaceStore _workspaceStore;
    readonly Workspace _savedWorkspace;
    readonly CancellationTokenSource _restoreCancel = new();
    Border? _restoreBar;
    TextBlock? _restoreText;
    DispatcherTimer? _placementTimer;
    WindowPlacement? _normalBounds;
    bool _restoring;

    /// <summary>
    /// Reads last time's workspace and puts the window where it was. The panes are restored with the rest of the
    /// layout, from the settings; the tabs wait for <see cref="OfferWorkspaceAsync"/>, once the window is open.
    /// </summary>
    void InitWorkspace()
    {
        ApplySavedPlacement();

        // The window's normal bounds are noted a moment after it settles, so a maximize (which moves and resizes the
        // window just before the state changes) isn't taken for where the window is.
        _placementTimer = new DispatcherTimer { Interval = PlacementDelay };
        _placementTimer.Tick += (_, _) =>
        {
            _placementTimer.Stop();
            NoteNormalBounds();
        };
        PositionChanged += (_, _) => RestartPlacementTimer();
        Resized += (_, _) => RestartPlacementTimer();

        _restoreBar = this.FindControl<Border>("RestoreBar")!;
        _restoreText = this.FindControl<TextBlock>("RestoreText")!;
        this.FindControl<Button>("RestoreReopenButton")!.Click += async (_, _) => await ReopenWorkspaceAsync();
        this.FindControl<Button>("RestoreDismissButton")!.Click += (_, _) => _restoreBar.IsVisible = false;
    }

    void ApplySavedPlacement()
    {
        if (_savedWorkspace.Window is not { } saved)
        {
            return;
        }

        var screens = Screens.All.OrderByDescending(s => s.IsPrimary).Select(s => new WindowPlacementFitter.ScreenArea(s.WorkingArea, s.Scaling)).ToList();
        var fit = WindowPlacementFitter.Fit(saved, screens, new Size(MinWidth, MinHeight));
        Width = fit.Size.Width;
        Height = fit.Size.Height;
        if (fit.Position is { } position)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
            _normalBounds = new WindowPlacement(position.X, position.Y, fit.Size.Width, fit.Size.Height, false);
        }

        if (saved.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    void RestartPlacementTimer()
    {
        _placementTimer?.Stop();
        _placementTimer?.Start();
    }

    void NoteNormalBounds()
    {
        if (WindowState == WindowState.Normal && !_fullScreen && IsVisible)
        {
            _normalBounds = new WindowPlacement(Position.X, Position.Y, ClientSize.Width, ClientSize.Height, false);
        }
    }

    /// <summary>The open tabs, in order, and the window as it is now. Holds nothing a login needs.</summary>
    internal Workspace CaptureWorkspace()
    {
        // Full screen leaves the window's own state until it ends.
        var state = _fullScreen ? _positionBeforeFullScreen?.State ?? _stateBeforeFullScreen : WindowState;
        NoteNormalBounds();
        var bounds = _normalBounds ?? new WindowPlacement(Position.X, Position.Y, Width, Height, false);
        WindowPlacement? window = double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
            ? bounds with { Maximized = state == WindowState.Maximized }
            : null;

        var tabs = _tabs.Select(tab => tab.Origin switch
        {
            // A local terminal comes back as its shell, not as a connection to this computer's name.
            { Local: true } local => new WorkspaceTab(null, null, 0, local.Protocol, LocalShell: local.LocalShell ?? Orrery.Protocols.Local.LocalShells.Find(null).Id),
            { EntryId: { } id } => new WorkspaceTab(id, null, 0, tab.Origin.Protocol, tab.Origin.Files),
            _ => new WorkspaceTab(null, tab.Session.Target.Host, tab.Session.Target.Port, tab.Origin.Protocol),
        }).ToList();
        return new Workspace { Window = window, Tabs = tabs, Active = _currentTab is null ? -1 : _tabs.IndexOf(_currentTab) };
    }

    /// <summary>Writes the workspace as the window closes; with reopening set to Never, only where the window was.</summary>
    void SaveWorkspace()
    {
        _restoreCancel.Cancel();
        var workspace = CaptureWorkspace();

        // Closing while still reopening last time's tabs, or with the offer to reopen them unanswered, shouldn't
        // forget them, unless they're not to be remembered at all.
        if (_settingsStore.Load().RestoreMode == RestoreSessionsMode.Never)
        {
            workspace = workspace.WithoutTabs();
        }
        else if (_restoring || (_restoreBar is { IsVisible: true } && _tabs.Count == 0))
        {
            workspace = workspace with { Tabs = _savedWorkspace.Tabs, Active = _savedWorkspace.Active };
        }

        _workspaceStore.Save(workspace);
        WorkspaceLog.LogInformation("Remembered {Count} open sessions for next time", workspace.Tabs.Count);
    }

    /// <summary>
    /// At start-up, once the window is open: reopens last time's tabs, or asks, or does nothing, as the setting says.
    /// Tabs for connections deleted since are left out.
    /// </summary>
    internal async Task OfferWorkspaceAsync()
    {
        var remembered = _savedWorkspace.Surviving(_tree);
        var mode = _settingsStore.Load().RestoreMode;
        if (mode == RestoreSessionsMode.Never)
        {
            // A list left from before Never was chosen (by hand in settings.json, or a crash) goes now.
            if (_savedWorkspace.Tabs.Count > 0)
            {
                ForgetOpenTabs();
            }

            return;
        }

        if (remembered.Tabs.Count == 0 || _restoreBar is null)
        {
            return;
        }

        if (mode == RestoreSessionsMode.Always)
        {
            await ReopenWorkspaceAsync();
            return;
        }

        _restoreText!.Text = remembered.Tabs.Count == 1 ? "1 session was open last time" : $"{remembered.Tabs.Count} sessions were open last time";
        _restoreBar.IsVisible = true;
        Controls.ScreenReader.Announce(_restoreBar, $"{_restoreText.Text}. Reopen them?", "Orrery.Restore", Controls.ScreenReader.MostRecent);
    }

    /// <summary>
    /// Reopening was set to Never: deletes the saved list of open tabs now, rather than at close, and withdraws an
    /// offer to reopen them. Where the window was stays.
    /// </summary>
    internal void ForgetOpenTabs()
    {
        if (_restoreBar is not null)
        {
            _restoreBar.IsVisible = false;
        }

        _workspaceStore.ForgetTabs();
        WorkspaceLog.LogInformation("Forgot the sessions that were open last time");
    }

    internal async Task ReopenWorkspaceAsync(TimeSpan? stagger = null)
    {
        if (_restoring)
        {
            return;
        }

        if (_restoreBar is not null)
        {
            _restoreBar.IsVisible = false;
        }

        var remembered = _savedWorkspace.Surviving(_tree);
        WorkspaceLog.LogInformation("Reopening {Count} sessions from last time", remembered.Tabs.Count);
        _restoring = true;
        try
        {
            var opened = await WorkspaceRestorer.OpenAllAsync(remembered.Tabs, ReopenTabAsync, stagger ?? WorkspaceRestorer.Stagger,
                Task.Delay, _restoreCancel.Token);
            if (remembered.Active >= 0 && opened[remembered.Active] is { } active && _tabs.Contains(active))
            {
                SelectTab(active);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// Opens one remembered tab as if it had been chosen: a saved connection with its current settings, a typed host
    /// directly, a local terminal with its shell (or the default one, if that shell has gone).
    /// </summary>
    async Task<SessionTab?> ReopenTabAsync(WorkspaceTab tab)
    {
        int before = _tabs.Count;
        if (tab.LocalShell is { } shell)
        {
            OpenLocalTerminal(shell);
        }
        else if (tab.EntryId is { } id && _tree.Find(id) is ConnectionEntry entry)
        {
            await ConnectAsync(entry, files: tab.Files);
        }
        else if (tab.Host is { } host)
        {
            await ConnectDirectAsync(new QuickConnectChoice(null, host, tab.Port, tab.Protocol));
        }

        return _tabs.Count > before ? _tabs[^1] : null;
    }
}

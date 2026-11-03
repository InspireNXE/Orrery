using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;
using Orrery.Core.Updates;

namespace Orrery.App.Updates;

/// <summary>What the update screens need from the main window.</summary>
interface IUpdateHost
{
    /// <summary>The window the update windows open over.</summary>
    Window Owner { get; }

    /// <summary>How many sessions are open, which a restart closes.</summary>
    int OpenSessions { get; }

    /// <summary>Whether the sessions open now come back after a restart: not when Settings says never to reopen them.</summary>
    bool ReopensSessions { get; }

    /// <summary>
    /// Saves what quitting saves, with the open sessions marked to reopen at the next start; false takes the mark
    /// off again, when the restart didn't happen.
    /// </summary>
    void PrepareToRestart(bool restarting);

    /// <summary>Ends Orrery, as quitting does.</summary>
    void Quit();

    /// <summary>A short note in the status bar.</summary>
    void ShowNotice(string text);
}

/// <summary>
/// Orrery's side of updating: checks from time to time while the main window is open (<see cref="UpdateSchedule"/>),
/// keeps the offer for the update bar, and takes an update from its details through the download to the restart. It
/// owns the engine for the window's life; disposing it cancels whatever is under way.
/// </summary>
sealed class UpdateController : IDisposable
{
    /// <summary>How long after the window opens the first automatic check waits, so start-up isn't slowed.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long after an automatic check fails it's tried again: it's still due, having not run.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    /// <summary>The longest the timer waits before looking again, so a computer that slept doesn't put a check off a day.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    static readonly ILogger UpdateLog = Log.For("Updates");

    readonly IUpdateService _service;
    readonly AppSettingsStore _settings;
    readonly TimeProvider _time;
    readonly CancellationTokenSource _closing = new();
    DispatcherTimer? _timer;
    CancellationTokenSource? _quietCancel;
    Task? _quietDownload;
    bool _checking;
    bool _disposed;
    ReleaseVersion? _hiddenVersion;
    DownloadedUpdate? _downloaded;
    DownloadedUpdate? _onQuit;

    // What a portable copy or AppImage starts once its new file is in place, kept so a second try doesn't swap again.
    (DownloadedUpdate Update, UpdateCommand Command)? _applied;

    public UpdateController(IUpdateService service, AppSettingsStore settings, TimeProvider? time = null)
    {
        _service = service;
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    public IUpdateHost? Host { get; set; }

    public ReleaseVersion CurrentVersion => _service.CurrentVersion;

    public CopyKind Kind => _service.Kind;

    /// <summary>Releases come from ORRERY_UPDATE_FEED, not GitHub; everything says "test feed" so it can't be mistaken.</summary>
    public bool UsesOtherFeed => _service.UsesOtherFeed;

    /// <summary>The newer release the last check found, or null.</summary>
    public UpdateOffer? Offer { get; private set; }

    /// <summary>The offer's file, downloaded and checked, ready to install; null until then.</summary>
    public DownloadedUpdate? Ready =>
        _downloaded is { } downloaded && Offer is { } offer && downloaded.Release.Version == offer.Release.Version ? downloaded : null;

    /// <summary>The update bar shows: there's an offer, and Later hasn't put it away until the next start.</summary>
    public bool BarShown => Offer is { } offer && offer.Release.Version != _hiddenVersion;

    /// <summary>"Orrery 0.2.0 is out", or "is ready" once it's downloaded, with "(test feed)" when the feed isn't GitHub's.</summary>
    public string BarText => Offer is not { } offer ? string.Empty
        : $"Orrery {offer.Release.Version} is {(Ready is null ? "out" : "ready")}{(UsesOtherFeed ? " (test feed)" : string.Empty)}";

    /// <summary>An update waits for Orrery to quit ("When I quit").</summary>
    public bool InstallsOnQuit => _onQuit is not null;

    /// <summary>The offer, its download or the bar changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts the automatic checks: the first a few seconds from now, if one is due.</summary>
    public void Start() => Schedule(FirstCheckDelay);

    /// <summary>The update settings changed: checks were turned on or off, say. Looks again shortly.</summary>
    public void SettingsChanged() => Schedule(FirstCheckDelay);

    /// <summary>How long until the timer looks again; null when it isn't running (checks are off).</summary>
    internal TimeSpan? NextLook => _timer is { IsEnabled: true } timer ? timer.Interval : null;

    void Schedule(TimeSpan atLeast)
    {
        _timer?.Stop();
        if (_disposed || UpdateSchedule.TimeUntilDue(_settings.Load(), _time.GetUtcNow()) is not { } until)
        {
            return;
        }

        var wait = until < atLeast ? atLeast : until;
        if (wait > LongestWait)
        {
            wait = LongestWait;
        }

        if (_timer is null)
        {
            _timer = new DispatcherTimer();
            _timer.Tick += async (_, _) =>
            {
                _timer.Stop();
                await CheckIfDueAsync();
            };
        }

        _timer.Interval = wait;
        _timer.Start();
    }

    /// <summary>
    /// The automatic check: runs when one is due, notes when it ran (not when it failed, so it's tried again), and sets
    /// the timer for the next. With Download chosen in Settings, a release found is downloaded in the background.
    /// Failures are logged, never shown.
    /// </summary>
    public async Task CheckIfDueAsync()
    {
        if (_checking || _disposed)
        {
            return;
        }

        var settings = _settings.Load();
        if (!UpdateSchedule.IsCheckDue(settings, _time.GetUtcNow()))
        {
            Schedule(TimeSpan.Zero);
            return;
        }

        _checking = true;
        try
        {
            var offer = await _service.CheckAsync(settings.OffersPreviewUpdates, settings.SkippedUpdateVersion, _closing.Token);
            SaveSettings(s => s with { LastUpdateCheck = _time.GetUtcNow() });
            if (offer is null)
            {
                UpdateLog.LogInformation("Orrery {Version} is up to date", CurrentVersion);
            }
            else
            {
                UpdateLog.LogInformation("Orrery {Version} is out", offer.Release.Version);
            }

            Show(offer);
            if (offer is { CanInstall: true } && settings.UpdateAction == UpdateAction.Download && Ready is null)
            {
                StartQuietDownload(offer);
            }
        }
        catch (UpdateException e)
        {
            UpdateLog.LogWarning("Couldn't check for updates: {Message}", e.Message);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            _checking = false;
        }

        Schedule(RetryDelay);
    }

    /// <summary>
    /// Checking by hand: the skipped version is offered again, and whatever is found shows in the bar, even after
    /// Later. Fails with <see cref="UpdateException"/>, whose message is fit to show.
    /// </summary>
    public async Task<UpdateOffer?> CheckNowAsync()
    {
        var offer = await _service.CheckAsync(_settings.Load().OffersPreviewUpdates, skippedVersion: null, _closing.Token);
        _hiddenVersion = null;
        Show(offer);
        return offer;
    }

    void Show(UpdateOffer? offer)
    {
        Offer = offer;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Puts the bar away until Orrery next starts. A newer release than this one still shows.</summary>
    public void Later()
    {
        _hiddenVersion = Offer?.Release.Version;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Doesn't offer this release again (<see cref="AppSettings.SkippedUpdateVersion"/>); a later one is offered.</summary>
    public void Skip()
    {
        if (Offer is not { } offer)
        {
            return;
        }

        SaveSettings(s => s with { SkippedUpdateVersion = offer.Release.Version.ToString() });
        UpdateLog.LogInformation("Skipping Orrery {Version}", offer.Release.Version);
        _quietCancel?.Cancel();
        if (_onQuit?.Release.Version == offer.Release.Version)
        {
            _onQuit = null;
        }

        Show(null);
    }

    /// <summary>The release's details, from the bar's "See what's new" or a check by hand, and what's chosen there.</summary>
    public async Task ShowDetailsAsync()
    {
        if (Offer is not { } offer || Host is not { } host)
        {
            return;
        }

        var window = new UpdateDetailsWindow(offer, CurrentVersion, Kind, UsesOtherFeed);
        switch (await window.ShowDialog<UpdateChoice?>(host.Owner))
        {
            case UpdateChoice.Update:
                await UpdateAsync();
                break;
            case UpdateChoice.Later:
                Later();
                break;
            case UpdateChoice.Skip:
                Skip();
                break;
        }
    }

    /// <summary>Downloads the offer with its progress showing, unless it's ready already, then restarts to install it.</summary>
    public async Task UpdateAsync()
    {
        if (Offer is not { CanInstall: true } offer || Host is not { } host)
        {
            return;
        }

        // A download under way in the background starts again where it can be seen.
        await StopQuietDownloadAsync();
        if (Ready is null)
        {
            var progress = new UpdateProgressWindow(offer);
            var downloaded = await progress.RunAsync(host.Owner, (report, token) => _service.DownloadAsync(offer, report, token), _closing.Token);
            if (downloaded is null)
            {
                return;
            }

            _downloaded = downloaded;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        await FinishAsync(host);
    }

    /// <summary>
    /// The restart step. With sessions open it asks first: restart now, or install when Orrery quits.
    /// </summary>
    async Task FinishAsync(IUpdateHost host)
    {
        if (Ready is not { } update)
        {
            return;
        }

        int open = host.OpenSessions;
        if (open > 0)
        {
            string sessions = open == 1 ? "1 session" : $"{open} sessions";
            string body = host.ReopensSessions ? $"{sessions} will close, and reopen after." : $"{sessions} will close.";
            if (!await Dialogs.AskAsync(host.Owner, "Restart to finish", body, "Restart now", "When I quit"))
            {
                _onQuit = update;
                UpdateLog.LogInformation("Orrery {Version} will be installed when Orrery quits", update.Release.Version);
                host.ShowNotice($"Orrery {update.Release.Version} will be installed when you quit");
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        await RestartAsync(update, host);
    }

    async Task RestartAsync(DownloadedUpdate update, IUpdateHost host)
    {
        UpdateCommand command;
        try
        {
            command = ApplyOnce(update);
        }
        catch (Exception e) when (e is UpdateException or InvalidOperationException)
        {
            UpdateLog.LogError("Couldn't install Orrery {Version}: {Message}", update.Release.Version, e.Message);
            _downloaded = null;
            Changed?.Invoke(this, EventArgs.Empty);
            await Dialogs.ShowMessageAsync(host.Owner, "Couldn't install the update", $"{e.Message}\n\nOrrery carries on as it is.");
            return;
        }

        _onQuit = null;
        host.PrepareToRestart(true);
        if (UpdateRunner.Start(command) is { } problem)
        {
            host.PrepareToRestart(false);
            UpdateLog.LogError("Couldn't start {File} to finish the update: {Message}", command.FileName, problem);
            await Dialogs.ShowMessageAsync(host.Owner, "Orrery didn't restart", command.RunsSetup
                ? $"Orrery Setup didn't start: {problem}\n\nIf you said no when asked for permission, choose Update again when you're ready. Orrery is still running."
                : $"The new Orrery didn't start: {problem}\n\nIt's in place, so it runs the next time you start Orrery. This one is still running.");
            return;
        }

        UpdateLog.LogInformation("Restarting to finish the update to {Version}", update.Release.Version);
        host.Quit();
    }

    /// <summary>
    /// The engine's apply, once per update for a portable copy or AppImage, whose new file is then already in place;
    /// Orrery Setup is checked again each time.
    /// </summary>
    UpdateCommand ApplyOnce(DownloadedUpdate update)
    {
        if (_applied is { } applied && applied.Update == update)
        {
            return applied.Command;
        }

        var command = _service.Apply(update);
        if (!command.RunsSetup)
        {
            _applied = (update, command);
        }

        return command;
    }

    /// <summary>
    /// Orrery is quitting normally: installs an update put off with "When I quit". Orrery Setup runs without starting
    /// Orrery again after, since it was quit; a portable copy or AppImage just has its new file put in place, which
    /// runs at the next start. Not called after a crash or for a restart Settings asked for.
    /// </summary>
    public void FinishOnQuit()
    {
        if (_onQuit is not { } update)
        {
            return;
        }

        _onQuit = null;
        try
        {
            var command = ApplyOnce(update);
            if (command.RunsSetup)
            {
                var quiet = command with { Arguments = [.. command.Arguments.Where(a => !a.StartsWith("/RELAUNCH", StringComparison.OrdinalIgnoreCase))] };
                if (UpdateRunner.Start(quiet) is { } problem)
                {
                    UpdateLog.LogError("Couldn't start Orrery Setup at quit: {Message}", problem);
                    return;
                }
            }

            UpdateLog.LogInformation("Installed Orrery {Version} on quitting", update.Release.Version);
        }
        catch (Exception e) when (e is UpdateException or InvalidOperationException)
        {
            UpdateLog.LogError("Couldn't install Orrery {Version} on quitting: {Message}", update.Release.Version, e.Message);
        }
    }

    void StartQuietDownload(UpdateOffer offer)
    {
        if (_quietDownload is { IsCompleted: false })
        {
            return;
        }

        _quietCancel = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _quietDownload = DownloadQuietlyAsync(offer, _quietCancel.Token);
    }

    async Task DownloadQuietlyAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        try
        {
            _downloaded = await _service.DownloadAsync(offer, null, cancellationToken);
            UpdateLog.LogInformation("Downloaded Orrery {Version}; it's ready to install", offer.Release.Version);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (UpdateException e)
        {
            UpdateLog.LogWarning("Couldn't download Orrery {Version}: {Message}", offer.Release.Version, e.Message);
        }
        catch (Exception e) when ((e is OperationCanceledException or ObjectDisposedException) && cancellationToken.IsCancellationRequested)
        {
            // Stopped: skipped, taken over by Update, or Orrery is closing.
        }
    }

    async Task StopQuietDownloadAsync()
    {
        if (_quietDownload is { IsCompleted: false } running)
        {
            _quietCancel?.Cancel();
            await running;
        }
    }

    void SaveSettings(Func<AppSettings, AppSettings> change)
    {
        try
        {
            _settings.Save(change(_settings.Load()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            UpdateLog.LogWarning("Couldn't save the update settings: {Message}", e.Message);
        }
    }

    /// <summary>Stops the timer and cancels any check or download under way.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Stop();
        _closing.Cancel();
        _service.Dispose();
    }
}

/// <summary>Starts what finishes an update, the way the engine says it must be started.</summary>
static class UpdateRunner
{
    /// <summary>
    /// Orrery Setup through the shell and without "runas": it asks for administrator rights itself, which is what lets
    /// it start Orrery again as the user. The new Orrery of a portable copy or AppImage directly, with its arguments.
    /// </summary>
    internal static ProcessStartInfo StartInfo(UpdateCommand command)
    {
        if (command.RunsSetup)
        {
            return new ProcessStartInfo(command.FileName, string.Join(' ', command.Arguments.Select(Quoted))) { UseShellExecute = true };
        }

        var start = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>Starts it; null if it started, what went wrong if not (setup turned down at the permission prompt, say).</summary>
    public static string? Start(UpdateCommand command)
    {
        try
        {
            using var process = Process.Start(StartInfo(command));
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return e.Message;
        }
    }

    static string Quoted(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

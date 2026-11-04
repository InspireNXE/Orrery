using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Orrery.App.Restore;
using Orrery.App.Sessions;
using Orrery.App.Settings;
using Orrery.Core.Settings;

namespace Orrery.App;

// Popping a session out of the tabs into a window of its own, and putting it back. A popped-out session stays among
// _tabs, in its place, so everything about open sessions (reconnecting, the tree's dots, the workspace) still counts
// it; only the strip, the session area and the current tab leave it out.
public partial class MainWindow
{
    /// <summary>Above the session in a popout, about the height of the main window's session toolbar.</summary>
    const double PopoutBarHeight = 46;

    /// <summary>Where a popout goes when none is given: a little down and right of the session area it left.</summary>
    const int PopoutOffset = 24;

    /// <summary>The tabs in the strip, in order: the open sessions less those popped out.</summary>
    List<SessionTab> StripTabs() => _tabs.Where(t => t.Popout is null).ToList();

    /// <summary>The window a session is shown in, for the dialogs and file pickers it opens.</summary>
    Window WindowOf(SessionTab tab) => tab.Popout ?? (Window)this;

    /// <summary>
    /// Moves <paramref name="tab"/> out of the strip into a window of its own: where it was last time
    /// (<paramref name="placement"/>), where a tab dragged off the strip was let go (<paramref name="at"/>, in screen
    /// pixels), or else over the session area, a little offset. It's the size of the session area, so a remote
    /// desktop keeps its size.
    /// </summary>
    void PopOut(SessionTab tab, WindowPlacement? placement = null, PixelPoint? at = null)
    {
        if (tab.Popout is not null || !_tabs.Contains(tab))
        {
            return;
        }

        bool current = tab == _currentTab;
        if (current && _fullScreen)
        {
            ExitFullScreen();
        }

        int index = StripTabs().IndexOf(tab);
        var partner = SplitPartner(tab);
        if (partner is not null)
        {
            EndSplit(select: false);
        }

        var area = _sessionHost.Bounds.Size;
        var origin = _sessionHost.PointToScreen(default);
        _tabStrip.Children.Remove(tab.Header);
        _sessionHost.Children.Remove(tab.Host);
        tab.Header.Classes.Set("selected", false);
        tab.Header.IsCurrent = false;

        var popout = new PopoutWindow(tab);
        tab.Popout = popout;
        WirePopout(popout);
        Place(popout, placement, at, area, origin);

        // A page drawn as a window of its own over Orrery's hides under Orrery's panes; this window has none.
        if (tab.View is WebPageView web)
        {
            web.Covered = false;
        }

        if (current || partner is not null)
        {
            var strip = StripTabs();
            SelectTab(partner ?? (strip.Count == 0 ? null : strip[Math.Min(index, strip.Count - 1)]), chosen: false);
        }

        UpdateSessionChrome();
        UpdatePopoutBar(popout);
        popout.Show();
        if (placement is { Maximized: true })
        {
            popout.WindowState = WindowState.Maximized;
        }

        ConnectLog.LogInformation("Popped {Title} out into its own window", tab.Origin.Title);
        Dispatcher.UIThread.Post(tab.FocusContent, DispatcherPriority.Background);
    }

    void Place(PopoutWindow popout, WindowPlacement? placement, PixelPoint? at, Size area, PixelPoint origin)
    {
        double scaling = RenderScaling;
        if (placement is not null)
        {
            var screens = Screens.All.OrderByDescending(s => s.IsPrimary).Select(s => new WindowPlacementFitter.ScreenArea(s.WorkingArea, s.Scaling)).ToList();
            var fit = WindowPlacementFitter.Fit(placement, screens, new Size(PopoutWindow.MinSessionWidth, PopoutWindow.MinSessionHeight));
            popout.Width = fit.Size.Width;
            popout.Height = fit.Size.Height;
            if (fit.Position is { } position)
            {
                popout.WindowStartupLocation = WindowStartupLocation.Manual;
                popout.Position = position;
            }
            else
            {
                popout.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            return;
        }

        popout.Width = area.Width >= PopoutWindow.MinSessionWidth ? area.Width : 960;
        popout.Height = (area.Height >= PopoutWindow.MinSessionHeight ? area.Height : 600) + PopoutBarHeight;
        popout.WindowStartupLocation = WindowStartupLocation.Manual;

        // Dropped: the pointer lands on the bar, near the title, as if holding the window by it.
        popout.Position = at is { } point
            ? new PixelPoint(point.X - (int)(60 * scaling), point.Y - (int)(20 * scaling))
            : new PixelPoint(origin.X + (int)(PopoutOffset * scaling), origin.Y + (int)(PopoutOffset * scaling));
    }

    /// <summary>
    /// Puts a popped-out session back in the strip, in the place it had or at <paramref name="stripIndex"/>, and shows
    /// it. <paramref name="closeWindow"/> is false when the window is already closing.
    /// </summary>
    void Rejoin(SessionTab tab, bool closeWindow = true, int? stripIndex = null)
    {
        if (tab.Popout is not { } popout)
        {
            return;
        }

        EndIncomingTab();
        if (stripIndex is { } wanted)
        {
            // Among all the open sessions, just before the tab it lands on, or after the last.
            var strip = StripTabs();
            _tabs.Remove(tab);
            int to = wanted < strip.Count ? _tabs.IndexOf(strip[Math.Max(wanted, 0)])
                : strip.Count == 0 ? _tabs.Count
                : _tabs.IndexOf(strip[^1]) + 1;
            _tabs.Insert(to, tab);
        }

        if (closeWindow)
        {
            popout.Release();
        }
        else
        {
            popout.TakeSession();
        }

        tab.Popout = null;
        int at = _tabs.TakeWhile(t => t != tab).Count(t => t.Popout is null);
        _tabStrip.Children.Insert(at, tab.Header);
        _sessionHost.Children.Add(tab.Host);
        if (_recordingsShown)
        {
            ShowRecordingsHost(false);
        }

        if (_settingsHost.IsVisible)
        {
            CloseSettings();
        }

        CoverWebPages();
        SelectTab(tab);
        UpdateSessionChrome();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _fullScreen ? WindowState.FullScreen : WindowState.Normal;
        }

        Activate();
        ConnectLog.LogInformation("Put {Title} back in the tabs", tab.Origin.Title);
    }

    /// <summary>A notice about a session where it can be seen: its window's bar if it's popped out, else the status bar.</summary>
    void ShowNotice(SessionTab tab, string text)
    {
        if (tab.Popout is { } popout)
        {
            popout.ShowNotice(text);
        }
        else
        {
            ShowNotice(text);
        }
    }

    /// <summary>Shows a session wherever it is: its tab, or its window brought to the front.</summary>
    void ShowTab(SessionTab tab)
    {
        if (tab.Popout is { } popout)
        {
            if (popout.WindowState == WindowState.Minimized)
            {
                popout.WindowState = WindowState.Normal;
            }

            popout.Activate();
            tab.MarkShown();
            Dispatcher.UIThread.Post(tab.FocusContent, DispatcherPriority.Background);
        }
        else
        {
            SelectTab(tab);
        }
    }

    /// <summary>Pops the tab out, or puts it back if it's out already.</summary>
    void TogglePopOut(SessionTab tab)
    {
        if (tab.Popout is null)
        {
            PopOut(tab);
        }
        else
        {
            Rejoin(tab);
        }
    }

    /// <summary>Full screen for the tab: its window's, if it's popped out, else the main window's.</summary>
    void ToggleFullScreen(SessionTab tab, Avalonia.Platform.Screen? screen = null)
    {
        if (tab.Popout is { } popout)
        {
            if (popout.IsFullScreen && screen is null)
            {
                popout.ExitFullScreen();
            }
            else
            {
                popout.ExitFullScreen();
                popout.EnterFullScreen(screen);
            }

            return;
        }

        SelectTab(tab);
        EnterFullScreen(screen);
    }

    void WirePopout(PopoutWindow popout)
    {
        popout.RejoinRequested += (_, _) => Rejoin(popout.Tab, closeWindow: false);
        popout.FullScreenChanged += (_, _) => UpdatePopoutBar(popout);
        popout.TabDragged += (_, at) => ShowIncomingTab(popout.Tab, at);
        popout.TabDropped += (_, at) =>
        {
            int? index = at is { } point ? IncomingIndex(point) : null;
            EndIncomingTab();

            // After the release has been through the window it came from, which closes.
            if (index is { } landing)
            {
                Dispatcher.UIThread.Post(() => Rejoin(popout.Tab, stripIndex: landing));
            }
        };

        // The main window's shortcuts that make sense for one session, caught on the way down as they are there.
        popout.AddHandler(KeyDownEvent, (_, e) =>
        {
            var tab = popout.Tab;
            bool mac = OperatingSystem.IsMacOS();
            var place = FocusPlaceOf(e.Source);
            if (e.Handled || (e.Source as Visual)?.FindAncestorOfType<ShortcutRecorder>(includeSelf: true) is { IsRecording: true })
            {
                return;
            }

            if (Input.FullScreenShortcut.Matches(_fullScreenShortcut, e.Key, e.KeyModifiers))
            {
                e.Handled = true;
                ToggleFullScreen(tab);
            }
            else if (Input.SessionEscape.Matches(e.Key, e.KeyModifiers))
            {
                // Out of the session to the bar, the popout's way back to the rest of the window.
                e.Handled = true;
                popout.FocusBar();
            }
            else if (Input.TabShortcuts.PopsOut(e.Key, e.KeyModifiers, place, mac))
            {
                e.Handled = true;
                Rejoin(tab);
            }
            else if (Input.TabShortcuts.Match(e.Key, e.KeyModifiers, place, mac) is { Kind: Input.TabCommandKind.Close })
            {
                e.Handled = true;
                _ = CloseTabAsync(tab.Session);
            }
            else if (Input.TabShortcuts.OpensLocalTerminal(e.Key, e.KeyModifiers, place, mac))
            {
                e.Handled = true;
                Activate();
                OpenLocalTerminal();
            }
            else if (Input.TabShortcuts.OpensQuickConnect(e.Key, e.KeyModifiers, place, mac)
                || (e.Key == Key.K && e.KeyModifiers == (mac ? KeyModifiers.Meta : KeyModifiers.Control) && place == Input.FocusPlace.App))
            {
                // As in the main window: plain Ctrl+K is a session's own (the shell's delete to the end of the line),
                // and Ctrl+Shift+K opens quick connect from a terminal.
                e.Handled = true;
                Activate();
                _ = OpenQuickConnectAsync();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    void UpdatePopoutBar(PopoutWindow popout)
    {
        popout.ShowState();
        ShowSessionBar(popout.Tab, popout.Address, popout.Lock, popout.Detail, popout.Actions);
    }

    /// <summary>The bar that shows <paramref name="tab"/>: its window's, or the main window's while it's the current tab.</summary>
    void RefreshSessionBar(SessionTab tab)
    {
        if (tab.Popout is { } popout)
        {
            UpdatePopoutBar(popout);
        }
        else if (tab == _currentTab)
        {
            UpdateSessionToolbar();
        }
    }

    // A popout's tab dragged over the strip: drawn following the pointer, with a slot opened where it would land.
    TabGhost? _incomingGhost;
    Border? _incomingSlot;

    /// <summary>
    /// Where among the strip's tabs a popout's tab dragged to <paramref name="at"/> (in screen pixels) would land, or
    /// null when that isn't on the strip. The slot opened for it doesn't count, so it doesn't push the answer along.
    /// </summary>
    int? IncomingIndex(PixelPoint at)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !_tabStripBar.IsVisible)
        {
            return null;
        }

        if (!new Rect(_tabStripBar.Bounds.Size).Inflate(new Thickness(0, 12)).Contains(_tabStripBar.PointToClient(at)))
        {
            return null;
        }

        double x = _tabStrip.PointToClient(at).X;
        int slotAt = _incomingSlot is { } slot ? _tabStrip.Children.IndexOf(slot) : -1;
        double slotSpace = slotAt >= 0 ? _incomingSlot!.Bounds.Width + _tabStrip.Spacing : 0;
        return StripTabs().Select((t, i) => t.Header.Bounds.Center.X - (slotAt >= 0 && i >= slotAt ? slotSpace : 0)).Count(middle => middle < x);
    }

    void ShowIncomingTab(SessionTab tab, PixelPoint at)
    {
        if (IncomingIndex(at) is not { } index)
        {
            EndIncomingTab();
            return;
        }

        // As wide as it was in the strip. Not measured now: measured out of the window, it would come back unstyled.
        double width = tab.Header.Bounds.Width > 0 ? tab.Header.Bounds.Width : 160;
        if (_incomingSlot is null)
        {
            _incomingSlot = new Border
            {
                Width = 0,
                IsHitTestVisible = false,
                Transitions = [new Avalonia.Animation.DoubleTransition { Property = WidthProperty, Duration = TabSlideTime }],
            };
            _incomingSlot.Classes.Add("tabslot");
            _incomingGhost = TabGhost.Show(this, tab, width);
        }

        if (_tabStrip.Children.IndexOf(_incomingSlot) != index)
        {
            _tabStrip.Children.Remove(_incomingSlot);
            _tabStrip.Children.Insert(index, _incomingSlot);
        }

        _incomingSlot.Width = width;
        var point = _tabStrip.PointToClient(at);
        // Level with the tabs, which hang 1 px below the strip over its bottom line.
        _incomingGhost?.MoveTo(_tabStrip, new Point(point.X - width / 2, _tabStrip.Bounds.Height + 1 - 32));
    }

    void EndIncomingTab()
    {
        if (_incomingSlot is { } slot)
        {
            _tabStrip.Children.Remove(slot);
        }

        _incomingGhost?.Remove();
        (_incomingSlot, _incomingGhost) = (null, null);
    }

    /// <summary>Closes the popped-out windows as Orrery quits, without putting their sessions back in the tabs.</summary>
    void ClosePopouts()
    {
        foreach (var tab in _tabs.Where(t => t.Popout is not null).ToList())
        {
            var popout = tab.Popout!;
            tab.Popout = null;
            popout.Release();
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Orrery.App.Sessions;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;

namespace Orrery.App;

/// <summary>
/// The side panel and the right-hand pane, each in a <see cref="PaneMode"/>. Pinned (Expanded), a pane sits beside
/// the sessions and takes room from them; otherwise it opens over them ("floating"), so a remote desktop keeps its size
/// and isn't asked to resize each time a pane is looked at. A floating pane closes on a click elsewhere or Escape, and
/// one opened by pointing at the window's edge also closes when the pointer leaves it. Ctrl+B and Ctrl+Alt+B only show
/// or hide a pane; the mode changes with its pin button, Settings, or a right-click on the buttons beside the tabs.
/// </summary>
public partial class MainWindow
{
    static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(400);
    static readonly BoxShadows FloatingShadow = BoxShadows.Parse("0 0 28 0 #99000000");
    const double DefaultSideWidth = 280, DefaultInspectorWidth = 320, MinPaneWidth = 220, MaxPaneWidth = 640;

    /// <summary>What the sessions keep at least, beside pinned panes, however wide those were set on a bigger screen.</summary>
    const double MinSessionWidth = 360;

    PaneMode _sideMode, _inspectorMode;
    bool _sideFloating, _inspectorFloating;

    // A pinned pane put away with its shortcut or toggle, until it's shown again. Saved, so it starts the way it was
    // left; the mode stays pinned.
    bool _sideHiddenForNow, _inspectorHiddenForNow;
    (bool Side, bool Inspector) _savedPutAway;

    // Opened by pointing rather than clicking, so leaving closes them.
    bool _sideHovered, _inspectorHovered;

    Border _leftEdge = null!, _rightEdge = null!, _inspectorStrip = null!;
    readonly ContentControl _inspectorContent = new();
    readonly List<Button> _sidePins = [];
    Button? _inspectorPin;
    readonly DispatcherTimer _sideLeave = new() { Interval = HoverCloseDelay };
    readonly DispatcherTimer _inspectorLeave = new() { Interval = HoverCloseDelay };

    void WirePanes()
    {
        _leftEdge = this.FindControl<Border>("LeftEdge")!;
        _rightEdge = this.FindControl<Border>("RightEdge")!;
        _inspectorStrip = this.FindControl<Border>("InspectorStrip")!;

        var settings = _settingsStore.Load();
        _sideMode = settings.SidePanel;
        _inspectorMode = settings.Inspector;
        _sideHiddenForNow = _sideMode == PaneMode.Expanded && settings.SidePanelPutAway == true;
        _inspectorHiddenForNow = _inspectorMode == PaneMode.Expanded && settings.InspectorPutAway == true;
        _savedPutAway = (_sideHiddenForNow, _inspectorHiddenForNow);
        _sidePanelToggle.ContextMenu = ModeMenu("Side panel", () => _sideMode, SetSidePanelMode);
        _inspectorToggle.ContextMenu = ModeMenu("Settings pane", () => _inspectorMode, SetInspectorMode);

        // A pin in each of the side panel's headers, and one in the right-hand pane's top corner.
        foreach (var header in new[] { _credentialsView.Header, _recentPanel.Header, _recordingsPanel.Header })
        {
            Controls.Pane.AddHeaderButton(header, SidePin());
        }

        this.FindControl<StackPanel>("ConnectionsHeaderButtons")!.Children.Insert(0, SidePin());
        _inspectorPin = PinButton(() => PinInspector(_inspectorMode != PaneMode.Expanded));
        _inspectorPin.HorizontalAlignment = HorizontalAlignment.Right;
        _inspectorPin.VerticalAlignment = VerticalAlignment.Top;
        _inspectorPin.Margin = new Thickness(0, 12, 10, 0);
        var inspectorPanel = (Panel)_propertiesHost.Child!;
        inspectorPanel.Children.Add(_inspectorPin);

        // Dragging a pane's inner edge sets its width, which is remembered.
        _sidePanel.Width = Math.Clamp(settings.SidePanelWidth ?? DefaultSideWidth, MinPaneWidth, MaxPaneWidth);
        _propertiesHost.Width = Math.Clamp(settings.InspectorWidth ?? DefaultInspectorWidth, MinPaneWidth, MaxPaneWidth);
        ((Panel)_sidePanel.Child!).Children.Add(ResizeGrip(_sidePanel, onLeft: false, DefaultSideWidth, (s, width) => s with { SidePanelWidth = width }));
        inspectorPanel.Children.Add(ResizeGrip(_propertiesHost, onLeft: true, DefaultInspectorWidth, (s, width) => s with { InspectorWidth = width }));
        SizeChanged += (_, _) => FitPanes();

        _leftEdge.PointerEntered += (_, _) => FloatSidePanel(hovered: true);
        _rightEdge.PointerEntered += (_, _) => FloatInspector(hovered: true);

        _sidePanel.PointerEntered += (_, _) => _sideLeave.Stop();
        _sidePanel.PointerExited += (_, _) => LeaveLater(_sideHovered, _sideLeave);
        _propertiesHost.PointerEntered += (_, _) => _inspectorLeave.Stop();
        _propertiesHost.PointerExited += (_, _) => LeaveLater(_inspectorHovered, _inspectorLeave);
        _sideLeave.Tick += (_, _) => LeaveFloating(_sideLeave, _sidePanel, () => _sideFloating = _sideHovered = false);
        _inspectorLeave.Tick += (_, _) => LeaveFloating(_inspectorLeave, _propertiesHost, () => _inspectorFloating = _inspectorHovered = false);

        _sidePanel.AddHandler(KeyDownEvent, OnFloatingKeyDown);
        _propertiesHost.AddHandler(KeyDownEvent, OnFloatingKeyDown);
        AddHandler(PointerPressedEvent, OnPanesPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The page it was left on, when that's Recent; Connections otherwise.
        if (settings.SidePanelPage == "recent")
        {
            _side = "recent";
            foreach (var (name, (_, view)) in _sides)
            {
                view.IsVisible = name == _side;
            }

            _recentPanel.Refresh();
        }

        ApplyPanes();
    }

    /// <summary>
    /// Whether the side panel sits beside the sessions: pinned and not put away, or while Recordings shows its player,
    /// which has nothing under it to keep the size of.
    /// </summary>
    bool SideDocked => (_sideMode == PaneMode.Expanded && !_sideHiddenForNow) || _recordingsShown;

    bool InspectorDocked => _inspectorMode == PaneMode.Expanded && !_inspectorHiddenForNow;

    /// <summary>
    /// A web page is showing that's drawn in a window of its own, which nothing of Orrery's can be drawn over (Linux
    /// and macOS), so a pane opened over it would only blank it; panes open beside it instead, and the page narrows to
    /// fit. On Windows the page is part of Orrery's picture and panes open over it as over any session.
    /// </summary>
    bool WebPageShowing => _currentTab?.View is WebPageView && !WebPageView.DrawnInOrrery && !_recordingsShown;

    /// <summary>Puts each pane where its mode and the sessions say: beside them, over them, or away.</summary>
    void ApplyPanes()
    {
        if (_fullScreen)
        {
            return;
        }

        bool sideDocked = SideDocked;
        _sideFloating &= !sideDocked;
        _rail.IsVisible = _sideMode != PaneMode.Hidden;
        _sidePanel.IsVisible = sideDocked || _sideFloating;
        Place(_sidePanel, sideDocked || (_sideFloating && WebPageShowing) ? 1 : null, HorizontalAlignment.Left);
        _leftEdge.IsVisible = _sideMode == PaneMode.Hidden && !_sideFloating;
        foreach (var (name, (button, _)) in _sides)
        {
            button.Classes.Set("active", _sidePanel.IsVisible && name == _side && !_settingsHost.IsVisible);
        }

        bool inspectorDocked = InspectorDocked;
        _inspectorFloating &= !inspectorDocked;
        _propertiesHost.IsVisible = inspectorDocked || _inspectorFloating;
        Place(_propertiesHost, inspectorDocked || (_inspectorFloating && WebPageShowing) ? 4 : null, HorizontalAlignment.Right);
        _inspectorStrip.IsVisible = !inspectorDocked && _inspectorMode != PaneMode.Hidden;
        _rightEdge.IsVisible = _inspectorMode == PaneMode.Hidden && !_inspectorFloating;

        foreach (var pin in _sidePins)
        {
            ShowPin(pin, "side panel", _sideMode == PaneMode.Expanded);
        }

        if (_inspectorPin is not null)
        {
            ShowPin(_inspectorPin, "settings pane", _inspectorMode == PaneMode.Expanded);
        }

        ToolTip.SetTip(_sidePanelToggle, Tip("side panel", "Ctrl+B", _sideMode, _sidePanel.IsVisible));
        ToolTip.SetTip(_inspectorToggle, Tip("settings pane", "Ctrl+Alt+B", _inspectorMode, _propertiesHost.IsVisible));
        FitPanes();
        SavePutAway();
    }

    /// <summary>Keeps whether each pinned pane is put away, when that changed, so the next start opens the same.</summary>
    void SavePutAway()
    {
        if ((_sideHiddenForNow, _inspectorHiddenForNow) == _savedPutAway)
        {
            return;
        }

        _savedPutAway = (_sideHiddenForNow, _inspectorHiddenForNow);
        SaveLayout(s => s with { SidePanelPutAway = _sideHiddenForNow ? true : null, InspectorPutAway = _inspectorHiddenForNow ? true : null });
    }

    void SaveLayout(Func<AppSettings, AppSettings> change)
    {
        try
        {
            _settingsStore.Save(change(_settingsStore.Load()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("App").LogWarning("Couldn't save the layout: {Message}", e.Message);
        }
    }

    /// <summary>The room pinned panes have together: the window less the rail and what the sessions keep.</summary>
    double PaneRoom => Bounds.Width - (_rail.IsVisible ? _rail.Bounds.Width : 0) - MinSessionWidth;

    /// <summary>
    /// Narrows pinned panes, both in proportion, when together they'd leave the sessions less than
    /// <see cref="MinSessionWidth"/>, as when widths saved on a big screen open on a small one. The widths themselves
    /// are kept, for when the window is wide again.
    /// </summary>
    void FitPanes()
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        double side = SideDocked ? _sidePanel.Width : 0, inspector = InspectorDocked ? _propertiesHost.Width : 0;
        double scale = side + inspector > PaneRoom ? Math.Max(0, PaneRoom) / (side + inspector) : 1;
        _sidePanel.MaxWidth = scale < 1 ? Math.Max(MinPaneWidth, side * scale) : double.PositiveInfinity;
        _propertiesHost.MaxWidth = scale < 1 ? Math.Max(MinPaneWidth, inspector * scale) : double.PositiveInfinity;
    }

    static string Tip(string pane, string keys, PaneMode mode, bool shown) =>
        (shown ? $"Hide the {pane}" : mode == PaneMode.Expanded ? $"Show the {pane}" : $"Show the {pane} over the session")
        + $" ({keys}). Right-click to choose how it shows.";

    /// <summary>Beside the sessions in <paramref name="dockColumn"/>, or over them against one edge when null.</summary>
    static void Place(Border pane, int? dockColumn, HorizontalAlignment edge)
    {
        Grid.SetColumn(pane, dockColumn ?? 2);
        pane.HorizontalAlignment = dockColumn is null ? edge : HorizontalAlignment.Stretch;
        pane.ZIndex = dockColumn is null ? 10 : 0;
        pane.BoxShadow = dockColumn is null ? FloatingShadow : default;
    }

    void HidePanesForFullScreen()
    {
        _sideFloating = _inspectorFloating = _sideHovered = _inspectorHovered = false;
        _sidePanel.IsVisible = _propertiesHost.IsVisible = false;
        _inspectorStrip.IsVisible = _leftEdge.IsVisible = _rightEdge.IsVisible = false;
    }

    // ---- Pins ----

    Button SidePin()
    {
        var pin = PinButton(() => PinSidePanel(_sideMode != PaneMode.Expanded));
        pin.Width = pin.Height = 28;
        _sidePins.Add(pin);
        return pin;
    }

    static Button PinButton(Action click)
    {
        var pin = new Button { Width = 28, Height = 28 };
        pin.Classes.Add("icon");
        pin.Click += (_, _) => click();
        return pin;
    }

    static void ShowPin(Button pin, string pane, bool pinned)
    {
        pin.Content = Controls.StrokeIcon.Create(pinned ? Controls.Icons.Unpin : Controls.Icons.Pin, 15);
        string tip = pinned ? $"Unpin the {pane}: it opens over the session instead" : $"Pin the {pane} beside the sessions";
        ToolTip.SetTip(pin, tip);
        Avalonia.Automation.AutomationProperties.SetName(pin, pinned ? $"Unpin the {pane}" : $"Pin the {pane}");
    }

    /// <summary>Pins the side panel, or unpins it and leaves it open over the session until a click elsewhere.</summary>
    void PinSidePanel(bool pin)
    {
        SetSidePanelMode(pin ? PaneMode.Expanded : PaneMode.Collapsed);
        if (!pin)
        {
            FloatSidePanel(hovered: false);
        }
    }

    void PinInspector(bool pin)
    {
        SetInspectorMode(pin ? PaneMode.Expanded : PaneMode.Collapsed);
        if (!pin)
        {
            FloatInspector(hovered: false);
        }
    }

    // ---- Width ----

    /// <summary>A strip along a pane's inner edge: drag it to change the pane's width, or double-click for the default.</summary>
    Border ResizeGrip(Border pane, bool onLeft, double defaultWidth, Func<AppSettings, double?, AppSettings> save)
    {
        var grip = new Border
        {
            Width = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            HorizontalAlignment = onLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            ZIndex = 1,
        };
        ToolTip.SetTip(grip, "Drag to change the width. Double-click for the usual width.");
        ToolTip.SetShowDelay(grip, 800);

        double startX = 0, startWidth = 0;
        bool dragging = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;
            if (e.ClickCount == 2)
            {
                pane.Width = defaultWidth;
                SaveWidth(null);
                return;
            }

            (dragging, startX, startWidth) = (true, e.GetPosition(this).X, pane.Width);
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (dragging)
            {
                double moved = e.GetPosition(this).X - startX;
                // The other pane, when it's pinned too, keeps its width: this one gets what's left of the room.
                var other = pane == _sidePanel ? (InspectorDocked ? _propertiesHost.Bounds.Width : 0) : (SideDocked ? _sidePanel.Bounds.Width : 0);
                double most = Math.Min(Math.Min(MaxPaneWidth, Bounds.Width * 0.45), PaneRoom - other);
                pane.Width = Math.Clamp(startWidth + (onLeft ? -moved : moved), MinPaneWidth, Math.Max(MinPaneWidth, most));
                FitPanes();
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            if (dragging)
            {
                dragging = false;
                e.Pointer.Capture(null);
                SaveWidth(pane.Width == defaultWidth ? null : Math.Round(pane.Width));
            }
        };
        grip.PointerCaptureLost += (_, _) => dragging = false;
        return grip;

        void SaveWidth(double? width)
        {
            try
            {
                _settingsStore.Save(save(_settingsStore.Load(), width));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.For("App").LogWarning("Couldn't save a pane's width: {Message}", e.Message);
            }
        }
    }

    // ---- Opening and closing ----

    void OnToggleSidePanelClick(object? sender, RoutedEventArgs e) => ToggleSidePanel();

    void OnToggleInspectorClick(object? sender, RoutedEventArgs e) => ToggleInspector();

    /// <summary>
    /// Ctrl+B and the toggle beside the tabs: hide the side panel, or show it (over the session unless it's pinned).
    /// The mode stays as it is.
    /// </summary>
    void ToggleSidePanel()
    {
        if (_sideMode == PaneMode.Expanded && !_recordingsShown)
        {
            _sideHiddenForNow = !_sideHiddenForNow;
            ApplyPanes();
        }
        else if (_sideFloating)
        {
            CloseFloatingPanes();
        }
        else
        {
            FloatSidePanel(hovered: false);
        }
    }

    /// <summary>Ctrl+Alt+B and its toggle, as <see cref="ToggleSidePanel"/> does for the side panel.</summary>
    void ToggleInspector()
    {
        if (_inspectorMode == PaneMode.Expanded)
        {
            if (!_inspectorHiddenForNow)
            {
                _properties.TrySave();
            }

            _inspectorHiddenForNow = !_inspectorHiddenForNow;
            ApplyPanes();
        }
        else if (_inspectorFloating)
        {
            CloseFloatingPanes();
        }
        else
        {
            FloatInspector(hovered: false);
        }
    }

    /// <summary>The rail's lit item clicked again: closes the panel over the session, or puts a pinned one away.</summary>
    void ClickActiveRailItem()
    {
        if (_sideFloating)
        {
            CloseFloatingPanes();
        }
        else if (SideDocked)
        {
            _sideHiddenForNow = true;
            ApplyPanes();
        }
    }

    /// <summary>Makes sure the side panel can be seen: beside the sessions when pinned, else over them.</summary>
    void RevealSidePanel()
    {
        _sideHiddenForNow = false;
        if (!SideDocked)
        {
            FloatSidePanel(hovered: false);
        }
        else
        {
            ApplyPanes();
        }
    }

    /// <summary>Makes sure the right-hand pane can be seen: beside the sessions when pinned, else over them.</summary>
    void RevealInspector()
    {
        _inspectorHiddenForNow = false;
        if (!InspectorDocked)
        {
            FloatInspector(hovered: false);
        }
        else
        {
            ApplyPanes();
        }
    }

    void OnInspectorStripClick(object? sender, RoutedEventArgs e)
    {
        if (_inspectorFloating)
        {
            CloseFloatingPanes();
        }
        else
        {
            RevealInspector();
        }
    }

    void FloatSidePanel(bool hovered)
    {
        if (_fullScreen || SideDocked)
        {
            return;
        }

        // A click keeps a panel that pointing opened; pointing doesn't take over one that was clicked open.
        _sideHovered = hovered && (!_sideFloating || _sideHovered);
        _sideFloating = true;
        ApplyPanes();
    }

    void FloatInspector(bool hovered)
    {
        if (_fullScreen || InspectorDocked)
        {
            return;
        }

        _inspectorHovered = hovered && (!_inspectorFloating || _inspectorHovered);
        _inspectorFloating = true;
        ApplyPanes();
    }

    void CloseFloatingPanes()
    {
        _sideLeave.Stop();
        _inspectorLeave.Stop();
        if (!_sideFloating && !_inspectorFloating)
        {
            return;
        }

        if (_inspectorFloating)
        {
            _properties.TrySave();
        }

        _sideFloating = _inspectorFloating = _sideHovered = _inspectorHovered = false;
        ApplyPanes();
    }

    static void LeaveLater(bool hovered, DispatcherTimer timer)
    {
        if (hovered)
        {
            timer.Start();
        }
    }

    /// <summary>The pointer left a pane it opened. Typing in it (the filter, a field) keeps it until a click elsewhere.</summary>
    void LeaveFloating(DispatcherTimer timer, Border pane, Action close)
    {
        timer.Stop();
        if (pane.IsKeyboardFocusWithin || pane.IsPointerOver)
        {
            return;
        }

        if (pane == _propertiesHost)
        {
            _properties.TrySave();
        }

        close();
        ApplyPanes();
    }

    void OnFloatingKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && (_sideFloating || _inspectorFloating))
        {
            e.Handled = true;
            CloseFloatingPanes();
            _currentTab?.FocusContent();
        }
    }

    /// <summary>A click outside the floating panes, and what opens them, closes them.</summary>
    void OnPanesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((!_sideFloating && !_inspectorFloating) || e.Source is not Visual source)
        {
            return;
        }

        Control[] keep = [_sidePanel, _propertiesHost, _rail, _inspectorStrip, _sidePanelToggle, _inspectorToggle];
        if (!keep.Any(control => control == source || control.IsVisualAncestorOf(source)))
        {
            CloseFloatingPanes();
        }
    }

    // ---- Modes ----

    void SetSidePanelMode(PaneMode mode)
    {
        _sideMode = mode;
        _sideFloating = _sideHovered = _sideHiddenForNow = false;
        SavePaneModes();
        ApplyPanes();
    }

    void SetInspectorMode(PaneMode mode)
    {
        if (mode != PaneMode.Expanded)
        {
            _properties.TrySave();
        }

        _inspectorMode = mode;
        _inspectorFloating = _inspectorHovered = _inspectorHiddenForNow = false;
        SavePaneModes();
        ApplyPanes();
    }

    /// <summary>Takes modes chosen in Settings, which saved them already.</summary>
    void FollowPaneModes(PaneMode side, PaneMode inspector)
    {
        (_sideMode, _inspectorMode) = (side, inspector);
        _sideFloating = _inspectorFloating = _sideHovered = _inspectorHovered = _sideHiddenForNow = _inspectorHiddenForNow = false;
        ApplyPanes();
    }

    void SavePaneModes() => SaveLayout(s => s with { SidePanelMode = _sideMode, InspectorMode = _inspectorMode });

    /// <summary>The toggle's right-click menu: the three modes, the current one checked.</summary>
    static ContextMenu ModeMenu(string pane, Func<PaneMode> current, Action<PaneMode> choose)
    {
        var items = PaneModes.All.Select(choice =>
        {
            var item = new MenuItem { Header = choice.Label, ToggleType = MenuItemToggleType.Radio, GroupName = pane, Tag = choice.Mode };
            ToolTip.SetTip(item, choice.Description);
            item.Click += (_, _) => choose(choice.Mode);
            return item;
        }).ToList();

        var menu = new ContextMenu { ItemsSource = items };
        Avalonia.Automation.AutomationProperties.SetName(menu, $"{pane} mode");
        menu.Opened += (_, _) =>
        {
            foreach (var item in items)
            {
                item.IsChecked = (PaneMode)item.Tag! == current();
            }
        };
        return menu;
    }
}

/// <summary>The pane modes as offered in menus and Settings.</summary>
static class PaneModes
{
    public static readonly (PaneMode Mode, string Label, string Description)[] All =
    [
        (PaneMode.Expanded, "Pinned", "Beside the sessions, taking room from them."),
        (PaneMode.Collapsed, "Over the session", "Icons only. Clicking one opens the pane over the session, which keeps its size."),
        (PaneMode.Hidden, "Hidden", "Nothing on screen. Its shortcut, or pointing at the window's edge, opens it over the session."),
    ];
}

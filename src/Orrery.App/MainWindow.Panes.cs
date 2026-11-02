using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;

namespace Orrery.App;

/// <summary>
/// The side panel and the right-hand pane, each in a <see cref="PaneMode"/>. Docked, a pane sits beside the sessions
/// and takes room from them; otherwise it opens over them ("floating"), so a remote desktop keeps its size and isn't
/// asked to resize each time a pane is looked at. A floating pane closes on a click elsewhere or Escape, and one opened
/// by pointing (at the rail in Auto, or the window's edge when hidden) also closes when the pointer leaves it.
/// </summary>
public partial class MainWindow
{
    static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(400);
    static readonly BoxShadows FloatingShadow = BoxShadows.Parse("0 0 28 0 #99000000");

    PaneMode _sideMode, _inspectorMode;
    bool _sideFloating, _inspectorFloating;

    // Opened by pointing rather than clicking, so leaving closes them.
    bool _sideHovered, _inspectorHovered;

    Border _leftEdge = null!, _rightEdge = null!, _inspectorStrip = null!;
    readonly DispatcherTimer _railHover = new() { Interval = HoverOpenDelay };
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
        _sidePanelToggle.ContextMenu = ModeMenu("Side panel", () => _sideMode, SetSidePanelMode);
        _inspectorToggle.ContextMenu = ModeMenu("Settings pane", () => _inspectorMode, SetInspectorMode);

        _leftEdge.PointerEntered += (_, _) => FloatSidePanel(hovered: true);
        _rightEdge.PointerEntered += (_, _) => FloatInspector(hovered: true);

        // In Auto, pointing at the rail slides the panel in over the session.
        _railHover.Tick += (_, _) =>
        {
            _railHover.Stop();
            FloatSidePanel(hovered: true);
        };
        _rail.PointerEntered += (_, _) =>
        {
            _sideLeave.Stop();
            if (_sideMode == PaneMode.Auto && !Docked(_sideMode) && !_sideFloating)
            {
                _railHover.Start();
            }
        };
        _rail.PointerExited += (_, _) =>
        {
            _railHover.Stop();
            LeaveLater(_sideHovered, _sideLeave);
        };

        _sidePanel.PointerEntered += (_, _) => _sideLeave.Stop();
        _sidePanel.PointerExited += (_, _) => LeaveLater(_sideHovered, _sideLeave);
        _propertiesHost.PointerEntered += (_, _) => _inspectorLeave.Stop();
        _propertiesHost.PointerExited += (_, _) => LeaveLater(_inspectorHovered, _inspectorLeave);
        _sideLeave.Tick += (_, _) => LeaveFloating(_sideLeave, _sidePanel, () => _sideFloating = _sideHovered = false);
        _inspectorLeave.Tick += (_, _) => LeaveFloating(_inspectorLeave, _propertiesHost, () => _inspectorFloating = _inspectorHovered = false);

        _sidePanel.AddHandler(KeyDownEvent, OnFloatingKeyDown);
        _propertiesHost.AddHandler(KeyDownEvent, OnFloatingKeyDown);
        AddHandler(PointerPressedEvent, OnPanesPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ApplyPanes();
    }

    /// <summary>Whether a pane in <paramref name="mode"/> sits beside the sessions right now.</summary>
    bool Docked(PaneMode mode) => mode == PaneMode.Expanded || (mode == PaneMode.Auto && _currentTab is null);

    /// <summary>Puts each pane where its mode and the sessions say: beside them, over them, or away.</summary>
    void ApplyPanes()
    {
        if (_fullScreen)
        {
            return;
        }

        bool sideDocked = Docked(_sideMode);
        _sideFloating &= !sideDocked;
        _rail.IsVisible = _sideMode != PaneMode.Hidden;
        _sidePanel.IsVisible = sideDocked || _sideFloating;
        Place(_sidePanel, sideDocked ? 1 : null, HorizontalAlignment.Left);
        _leftEdge.IsVisible = _sideMode == PaneMode.Hidden && !_sideFloating;
        foreach (var (name, (button, _)) in _sides)
        {
            button.Classes.Set("active", _sidePanel.IsVisible && name == _side && !_settingsHost.IsVisible);
        }

        bool inspectorDocked = Docked(_inspectorMode);
        _inspectorFloating &= !inspectorDocked;
        _propertiesHost.IsVisible = inspectorDocked || _inspectorFloating;
        Place(_propertiesHost, inspectorDocked ? 4 : null, HorizontalAlignment.Right);
        _inspectorStrip.IsVisible = !inspectorDocked && _inspectorMode != PaneMode.Hidden;
        _rightEdge.IsVisible = _inspectorMode == PaneMode.Hidden && !_inspectorFloating;

        ToolTip.SetTip(_sidePanelToggle, Tip("side panel", "Ctrl+B", _sideMode, sideDocked));
        ToolTip.SetTip(_inspectorToggle, Tip("settings pane", "Ctrl+Alt+B", _inspectorMode, inspectorDocked));
    }

    static string Tip(string pane, string keys, PaneMode mode, bool docked) =>
        (mode, docked) switch
        {
            (PaneMode.Expanded, _) => $"Collapse the {pane} ({keys})",
            (PaneMode.Collapsed, _) => $"Expand the {pane} ({keys})",
            (_, true) => $"The {pane} is {mode.ToString().ToLowerInvariant()}",
            _ => $"Show the {pane} over the session ({keys})",
        } + ". Right-click to choose how it shows.";

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

    // ---- Opening and closing ----

    void OnToggleSidePanelClick(object? sender, RoutedEventArgs e) => ToggleSidePanel();

    void OnToggleInspectorClick(object? sender, RoutedEventArgs e) => ToggleInspector();

    /// <summary>Ctrl+B and the toggle beside the tabs: collapse or expand, or show the panel over the session.</summary>
    void ToggleSidePanel()
    {
        switch (_sideMode)
        {
            case PaneMode.Expanded:
                SetSidePanelMode(PaneMode.Collapsed);
                break;
            case PaneMode.Collapsed:
                SetSidePanelMode(PaneMode.Expanded);
                break;
            default:
                if (_sideFloating)
                {
                    CloseFloatingPanes();
                }
                else
                {
                    FloatSidePanel(hovered: false);
                }

                break;
        }
    }

    /// <summary>Ctrl+Alt+B and its toggle, as <see cref="ToggleSidePanel"/> does for the side panel.</summary>
    void ToggleInspector()
    {
        switch (_inspectorMode)
        {
            case PaneMode.Expanded:
                SetInspectorMode(PaneMode.Collapsed);
                break;
            case PaneMode.Collapsed:
                SetInspectorMode(PaneMode.Expanded);
                break;
            default:
                if (_inspectorFloating)
                {
                    CloseFloatingPanes();
                }
                else
                {
                    FloatInspector(hovered: false);
                }

                break;
        }
    }

    /// <summary>The rail's lit item clicked again: closes the panel over the session, or collapses an expanded one.</summary>
    void ClickActiveRailItem()
    {
        if (_sideFloating)
        {
            CloseFloatingPanes();
        }
        else if (_sideMode == PaneMode.Expanded)
        {
            SetSidePanelMode(PaneMode.Collapsed);
        }
    }

    /// <summary>Makes sure the side panel can be seen: as it is when docked, else over the sessions.</summary>
    void RevealSidePanel()
    {
        if (!Docked(_sideMode))
        {
            FloatSidePanel(hovered: false);
        }
        else
        {
            ApplyPanes();
        }
    }

    /// <summary>Makes sure the right-hand pane can be seen: as it is when docked, else over the sessions.</summary>
    void RevealInspector()
    {
        if (!Docked(_inspectorMode))
        {
            FloatInspector(hovered: false);
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
            FloatInspector(hovered: false);
        }
    }

    void FloatSidePanel(bool hovered)
    {
        if (_fullScreen || Docked(_sideMode))
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
        if (_fullScreen || Docked(_inspectorMode))
        {
            return;
        }

        _inspectorHovered = hovered && (!_inspectorFloating || _inspectorHovered);
        _inspectorFloating = true;
        ApplyPanes();
    }

    void CloseFloatingPanes()
    {
        _railHover.Stop();
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
        _sideFloating = _sideHovered = false;
        SavePaneModes();
        ApplyPanes();
    }

    void SetInspectorMode(PaneMode mode)
    {
        if (!Docked(mode))
        {
            _properties.TrySave();
        }

        _inspectorMode = mode;
        _inspectorFloating = _inspectorHovered = false;
        SavePaneModes();
        ApplyPanes();
    }

    /// <summary>Takes modes chosen in Settings, which saved them already.</summary>
    void FollowPaneModes(PaneMode side, PaneMode inspector)
    {
        (_sideMode, _inspectorMode) = (side, inspector);
        _sideFloating = _inspectorFloating = _sideHovered = _inspectorHovered = false;
        ApplyPanes();
    }

    void SavePaneModes()
    {
        try
        {
            _settingsStore.Save(_settingsStore.Load() with { SidePanelMode = _sideMode, InspectorMode = _inspectorMode });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("App").LogWarning("Couldn't save the layout: {Message}", e.Message);
        }
    }

    /// <summary>The toggle's right-click menu: the four modes, the current one checked.</summary>
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
        (PaneMode.Expanded, "Expanded", "Always beside the sessions."),
        (PaneMode.Collapsed, "Collapsed", "Icons only. Clicking one opens the pane over the session, which keeps its size."),
        (PaneMode.Auto, "Auto", "Expanded while no session is showing. Collapsed while one is; pointing at the rail opens it over the session."),
        (PaneMode.Hidden, "Hidden", "Nothing on screen. Its shortcut, or pointing at the window's edge, opens it over the session."),
    ];
}

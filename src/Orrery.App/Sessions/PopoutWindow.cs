using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Settings;

namespace Orrery.App.Sessions;

/// <summary>
/// A session popped out of the tab strip into a window of its own, under a slim bar that says what it is and where
/// it's connected, with the session's actions. The main window keeps the session and fills the bar's address, lock,
/// detail and actions, as it does its own session toolbar. Full screen fills this window's display, and the bar
/// comes back over the session when the pointer reaches the top. Closing the window puts the session back in the
/// tabs rather than ending it (<see cref="RejoinRequested"/>).
/// </summary>
sealed class PopoutWindow : Window, IFullScreenHost
{
    public const double MinSessionWidth = 480, MinSessionHeight = 320;

    readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    readonly Panel _sessionArea = new() { Background = new SolidColorBrush(Color.Parse("#0B0D10")) };
    readonly Border _bar;
    readonly Border _handle;
    readonly Border _hotZone = new() { Height = 6, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, IsVisible = false, ZIndex = 2 };
    readonly StateGlyph _state = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _protocol = new() { FontFamily = Dialogs.MonoFont, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly TextBlock _notice = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
    int _noticeVersion;
    WindowState _stateBeforeFullScreen;
    (PixelPoint Position, WindowState State)? _positionBeforeFullScreen;

    // Set once the session has been taken out, so closing doesn't ask for it back in the tabs.
    bool _released;

    // The handle held down, and where; dragging once it has moved a little, with the tab drawn following it.
    Point? _handlePress;
    bool _handleDragging;
    TabGhost? _ghost;

    public PopoutWindow(SessionTab tab)
    {
        Tab = tab;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Orrery/Assets/orrery.ico")));
        FontSize = 13;
        MinWidth = MinSessionWidth;
        MinHeight = MinSessionHeight;
        Themed(this, BackgroundProperty, "OrreryBackground");
        Themed(this, ForegroundProperty, "OrreryText");

        Address.Classes.Add("mono");
        Themed(_notice, TextBlock.ForegroundProperty, "OrrerySoftText");
        Themed(Address, TextBlock.ForegroundProperty, "OrrerySoftText");

        // The session's name, shaped as a tab: dragged onto Orrery's tabs, it goes back among them where it's let go.
        var grip = StrokeIcon.Create(Icons.Grip, 14);
        grip.StrokeThickness = 2.5;
        Themed(grip, StrokeIcon.ForegroundProperty, "OrreryMuted");
        _handle = new Border
        {
            Padding = new Thickness(6, 4, 10, 4),
            Margin = new Thickness(-8, -4, 6, -4),
            // Filled, so the gaps between its parts can be held too.
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Background = Brushes.Transparent, Children = { grip, _state, _title, _protocol } },
        };
        _handle.Classes.Add("tabhandle");
        ToolTip.SetTip(_handle, "Drag onto Orrery's tabs to put it back there");
        _handle.PointerPressed += OnHandlePressed;
        _handle.PointerMoved += OnHandleMoved;
        _handle.PointerReleased += OnHandleReleased;
        _handle.PointerCaptureLost += (_, _) => EndHandleDrag(null);

        _bar = new Border
        {
            Padding = new Thickness(14, 7),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(Actions, Dock.Right),

                    // Outside the panel below, which cuts off what overhangs it: the handle reaches past the bar's padding.
                    Docked(_handle, Dock.Left),

                    // Docked rather than stacked, so in a narrow window the address and detail are cut short with an
                    // ellipsis instead of running under the buttons.
                    new DockPanel
                    {
                        ClipToBounds = true,
                        Margin = new Thickness(0, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            Docked(new DockPanel { Margin = new Thickness(0, 0, 14, 0), Children = { Docked(Lock, Dock.Left), Address } }, Dock.Left),
                            new Panel { Children = { Detail, _notice } },
                        },
                    },
                },
            },
        };
        Themed(_bar, Border.BackgroundProperty, "OrreryPanel");
        Themed(_bar, Border.BorderBrushProperty, "OrreryBorder");

        Grid.SetRow(_sessionArea, 1);
        Grid.SetRow(_hotZone, 1);
        _root.Children.Add(_bar);
        _root.Children.Add(_sessionArea);
        _root.Children.Add(_hotZone);
        Content = _root;

        // In full screen the bar slides in over the session from the top, and goes when the pointer leaves it.
        _hotZone.PointerEntered += (_, _) => _bar.IsVisible = true;
        _bar.PointerExited += (_, _) =>
        {
            if (IsFullScreen)
            {
                _bar.IsVisible = false;
            }
        };

        // Noted a moment after the window settles, as the main window does, so a maximize (which moves and resizes
        // the window just before its state changes) isn't taken for where the window is.
        _placementTimer.Tick += (_, _) =>
        {
            _placementTimer.Stop();
            NoteNormalBounds();
        };
        PositionChanged += (_, _) => RestartPlacementTimer();
        Resized += (_, _) => RestartPlacementTimer();
        Opened += (_, _) => NoteNormalBounds();
        Closed += (_, _) => _placementTimer.Stop();

        Hold(tab);
    }

    /// <summary>The session in this window.</summary>
    public SessionTab Tab { get; private set; }

    public TextBlock Address { get; } = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    public TextBlock Detail { get; } = new()
    {
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#8A93A3")),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    public StrokeIcon Lock { get; } = new() { Width = 14, Height = 14, StrokeThickness = 2, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 4 };

    public bool IsFullScreen { get; private set; }

    /// <summary>The window's close button was used: the session should go back in the tabs. The window closes after.</summary>
    public event EventHandler? RejoinRequested;

    /// <summary>Full screen started or ended.</summary>
    public event EventHandler? FullScreenChanged;

    /// <summary>The bar's tab is being dragged, and the pointer is here on the screen.</summary>
    public event EventHandler<PixelPoint>? TabDragged;

    /// <summary>The bar's tab was let go here on the screen, or the drag was given up (null).</summary>
    public event EventHandler<PixelPoint?>? TabDropped;

    /// <summary>Shows <paramref name="tab"/> here in place of the session before, as when a reconnect opens a new one.</summary>
    public void Hold(SessionTab tab)
    {
        _sessionArea.Children.Remove(Tab.Host);
        Tab = tab;
        tab.Host.IsVisible = true;
        _sessionArea.Children.Add(tab.Host);
        _title.Text = tab.Origin.Title;
        _protocol.Text = tab.Origin.Label;
        _protocol.Foreground = ProtocolStyle.Foreground(tab.Origin.Protocol);
        Title = $"{tab.Origin.Title} — Orrery";
        ShowState();
    }

    /// <summary>The state mark before the title, with the state in words for its tip and screen readers.</summary>
    public void ShowState()
    {
        _state.State = Tab.Session.State;
        ToolTip.SetTip(_state, Tab.StateText);
        Avalonia.Automation.AutomationProperties.SetName(_state, Tab.StateText);
    }

    /// <summary>
    /// Says something about the session for a few seconds in place of the detail (a recording stopped, say), as the main
    /// window does in its status bar, which can't be seen from here.
    /// </summary>
    public async void ShowNotice(string text)
    {
        int version = ++_noticeVersion;
        _notice.Text = text;
        _notice.IsVisible = true;
        Detail.IsVisible = false;
        ScreenReader.Announce(_notice, text, "Orrery.Notice", ScreenReader.MostRecent);
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (version == _noticeVersion)
        {
            _notice.IsVisible = false;
            Detail.IsVisible = true;
        }
    }

    /// <summary>Takes the session out of the window, so it can go back in the tabs; closing then leaves it alone.</summary>
    public void TakeSession()
    {
        _released = true;
        _sessionArea.Children.Remove(Tab.Host);
    }

    /// <summary>Takes the session out and closes the window.</summary>
    public void Release()
    {
        TakeSession();
        Close();
    }

    /// <summary>Moves the keyboard focus to the bar's first button: the way out of a session that keeps every key.</summary>
    public void FocusBar()
    {
        if (IsFullScreen)
        {
            ExitFullScreen();
        }

        Dispatcher.UIThread.Post(() => Actions.Children.OfType<Control>().FirstOrDefault(c => c.IsEffectivelyEnabled && c.Focusable)?.Focus(Avalonia.Input.NavigationMethod.Tab),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Fills the screen the window is on, or <paramref name="screen"/>: the window goes there first, and comes back
    /// after. Wayland doesn't let a window move itself, so there it's the display the window is on.
    /// </summary>
    public void EnterFullScreen(Screen? screen = null)
    {
        if (IsFullScreen)
        {
            return;
        }

        _positionBeforeFullScreen = null;
        if (screen is not null && !Equals(Screens.ScreenFromWindow(this)?.Bounds, screen.Bounds))
        {
            _positionBeforeFullScreen = (Position, WindowState);
            WindowState = WindowState.Normal;
            Position = new PixelPoint(screen.WorkingArea.X + 40, screen.WorkingArea.Y + 40);
        }

        _stateBeforeFullScreen = WindowState;
        IsFullScreen = true;

        // The bar lies over the session rather than above it, so showing it doesn't resize a remote desktop.
        Grid.SetRow(_bar, 1);
        _bar.VerticalAlignment = VerticalAlignment.Top;
        _bar.ZIndex = 3;
        _bar.IsVisible = false;
        _hotZone.IsVisible = true;
        WindowState = WindowState.FullScreen;
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
        Dispatcher.UIThread.Post(Tab.FocusContent, DispatcherPriority.Background);
    }

    public void ExitFullScreen()
    {
        if (!IsFullScreen)
        {
            return;
        }

        IsFullScreen = false;
        _hotZone.IsVisible = false;
        Grid.SetRow(_bar, 0);
        _bar.ClearValue(VerticalAlignmentProperty);
        _bar.ZIndex = 0;
        _bar.IsVisible = true;
        if (_positionBeforeFullScreen is { } before)
        {
            WindowState = WindowState.Normal;
            Position = before.Position;
            _stateBeforeFullScreen = before.State;
            _positionBeforeFullScreen = null;
        }

        WindowState = _stateBeforeFullScreen;
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
        Dispatcher.UIThread.Post(Tab.FocusContent, DispatcherPriority.Background);
    }

    WindowPlacement? _normalBounds;

    void RestartPlacementTimer()
    {
        _placementTimer.Stop();
        _placementTimer.Start();
    }

    void NoteNormalBounds()
    {
        if (WindowState == WindowState.Normal && !IsFullScreen && IsVisible)
        {
            _normalBounds = new WindowPlacement(Position.X, Position.Y, ClientSize.Width, ClientSize.Height, false);
        }
    }

    /// <summary>
    /// Where the window is, to open it there next time: its normal bounds, and whether it's maximized (as it was before
    /// full screen, while full screen lasts). Null before it has been shown.
    /// </summary>
    public WindowPlacement? Placement
    {
        get
        {
            var state = IsFullScreen ? _positionBeforeFullScreen?.State ?? _stateBeforeFullScreen : WindowState;
            NoteNormalBounds();
            return _normalBounds is { } bounds && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
                ? bounds with { Maximized = state == WindowState.Maximized }
                : null;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Only the user closing it puts the session back: Orrery quitting, or the computer shutting down, ends it.
        if (!e.Cancel && !_released && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            RejoinRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_handle).Properties.IsLeftButtonPressed && !IsFullScreen)
        {
            _handlePress = e.GetPosition(this);
            e.Pointer.Capture(_handle);
            e.Handled = true;
        }
    }

    void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_handlePress is not { } start)
        {
            return;
        }

        var at = e.GetPosition(this);
        if (!_handleDragging && (Math.Abs(at.X - start.X) > 6 || Math.Abs(at.Y - start.Y) > 6))
        {
            _handleDragging = true;
            _handle.Classes.Add("dragging");
            _handle.Cursor = new Cursor(StandardCursorType.DragMove);
            _ghost = TabGhost.Show(this, Tab, _handle.Bounds.Width);
        }

        if (_handleDragging)
        {
            _ghost?.MoveTo(this, at - new Vector(_handle.Bounds.Width / 2, 16));
            TabDragged?.Invoke(this, this.PointToScreen(at));
        }
    }

    void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_handlePress is not null)
        {
            e.Handled = true;
            EndHandleDrag(_handleDragging ? this.PointToScreen(e.GetPosition(this)) : null);
        }
    }

    void EndHandleDrag(PixelPoint? droppedAt)
    {
        if (_handlePress is null)
        {
            return;
        }

        bool dragged = _handleDragging;
        _handlePress = null;
        _handleDragging = false;
        _ghost?.Remove();
        _ghost = null;
        _handle.Classes.Remove("dragging");
        _handle.ClearValue(CursorProperty);
        if (dragged)
        {
            TabDropped?.Invoke(this, droppedAt);
        }
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    /// <summary>Follows a palette brush, so a different palette (color-blind safe, say) reaches this window too.</summary>
    static T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }
}

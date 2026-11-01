using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

/// <summary>
/// Where a session came from: its tab title, and how to open it again after it ends. <see cref="Restart"/>,
/// set when it should reconnect by itself, opens it again with the same login and without asking anything.
/// </summary>
sealed record SessionOrigin(string Title, Protocol Protocol, Guid? EntryId, Func<Task> Reconnect, bool AutoReconnect = false)
{
    public Func<(Control View, IConnectionSession Session)>? Restart { get; init; }

    /// <summary>SSH: a file browser (SFTP) instead of a terminal.</summary>
    public bool Files { get; init; }
}

/// <summary>
/// One open session: its view, and its header in the tab strip with a status dot, the protocol and a
/// close button. The header raises <see cref="Selected"/> and <see cref="CloseRequested"/>.
/// </summary>
sealed class SessionTab
{
    static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#5FD38D")).ToImmutable();
    static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.Parse("#E0B45A")).ToImmutable();
    static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#FF9AA2")).ToImmutable();
    static readonly IBrush EndedBrush = new SolidColorBrush(Color.Parse("#5B6372")).ToImmutable();
    static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#5B9DFF")).ToImmutable();

    readonly Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    // The current tab's top edge: drawn over the tab's own outline (hence the -1 margin) so it
    // follows the rounded corners instead of sitting inside them.
    readonly Border _accent = new()
    {
        Margin = new Thickness(-1, -1, -1, 0),
        BorderThickness = new Thickness(0, 2, 0, 0),
        CornerRadius = new CornerRadius(8, 8, 0, 0),
        BorderBrush = AccentBrush,
        IsHitTestVisible = false,
        IsVisible = false,
    };

    public SessionTab(IConnectionSession session, Control view, SessionOrigin origin)
    {
        Session = session;
        View = view;
        Origin = origin;

        var title = new TextBlock { Text = origin.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        var protocol = new TextBlock
        {
            Text = origin.Files ? "SFTP" : ProtocolInfo.Label(origin.Protocol),
            FontFamily = Dialogs.MonoFont,
            FontSize = 10,
            Foreground = ProtocolStyle.Foreground(origin.Protocol),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var close = new Button { Content = StrokeIcon.Create(Icons.Close, 12), VerticalAlignment = VerticalAlignment.Center };
        close.Classes.Add("icon");
        close.Classes.Add("tabclose");
        ToolTip.SetTip(close, "Disconnect and close");
        Avalonia.Automation.AutomationProperties.SetName(close, $"Close {origin.Title}");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };

        Header = new SessionTabHeader
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = new Grid
            {
                Children =
                {
                    _accent,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(12, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { _dot, title, protocol, close },
                    },
                },
            },
        };
        Header.Classes.Add("tab");
        Header.Label = $"{origin.Title}, {protocol.Text}";
        Avalonia.Automation.AutomationProperties.SetHelpText(Header, Address);
        ToolTip.SetTip(Header, Address);
        Header.Click += (_, _) => Selected?.Invoke(this, EventArgs.Empty);

        // Middle-click closes the tab, as in a browser: pressed and released over the same tab.
        bool middlePressed = false;
        Header.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(Header).Properties.IsMiddleButtonPressed)
            {
                middlePressed = true;
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        Header.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Middle || !middlePressed)
            {
                return;
            }

            middlePressed = false;
            e.Handled = true;
            if (new Rect(Header.Bounds.Size).Contains(e.GetPosition(Header)))
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }, RoutingStrategies.Tunnel);

        session.StateChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (e.State == SessionState.Connected)
            {
                HasConnected = true;
            }

            LastMessage = e.Message ?? LastMessage;
            ShowState();
            StateChanged?.Invoke(this, EventArgs.Empty);
        });

        _countdown.Tick += (_, _) => CountDown();
        Host = new Grid { Children = { view, BuildOverlay() } };
        ShowState();
    }

    public IConnectionSession Session { get; }
    public Control View { get; }
    public SessionOrigin Origin { get; }
    public SessionTabHeader Header { get; }

    /// <summary>The view with the reconnect notice over it; what goes in the session area.</summary>
    public Control Host { get; }

    /// <summary>Whether this session got as far as connected, as opposed to failing on the way.</summary>
    public bool HasConnected { get; private set; }

    /// <summary>Which automatic try this session is: 0 for one the user opened.</summary>
    public int ReconnectAttempt { get; init; }

    /// <summary>Set when the user disconnected or closed it, so its ending isn't treated as a drop.</summary>
    public bool EndedByUser { get; set; }

    /// <summary>Why it last failed, from the session.</summary>
    public string? LastMessage { get; private set; }

    public bool IsReconnecting => _overlay.IsVisible;

    /// <summary>
    /// Whether this should reconnect by itself: it can, it failed after connecting (or while an automatic try was
    /// connecting), the user didn't end it, and the server didn't refuse the login. A logoff ends as Disconnected
    /// and isn't retried.
    /// </summary>
    public bool WasCutOff =>
        Session.State == SessionState.Failed && Origin.Restart is not null && !EndedByUser
        && (HasConnected || ReconnectAttempt > 0)
        && Session is not Orrery.Protocols.Ssh.SshSession { AuthenticationRejected: true };

    // ---- Reconnect notice ----

    readonly Border _overlay = new() { IsVisible = false };
    readonly TextBlock _overlayReason = new() { TextWrapping = TextWrapping.Wrap, Foreground = Dialogs.MutedBrush, MaxWidth = 380, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _overlayCountdown = new() { HorizontalAlignment = HorizontalAlignment.Center };
    readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _reconnectAt;
    string _attemptText = string.Empty;
    Action? _reconnectNow;

    Control BuildOverlay()
    {
        var now = new Button { Content = "Reconnect now" };
        now.Classes.Add("accent");
        now.Click += (_, _) => Fire();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => CancelReconnect();

        _overlay.Background = new SolidColorBrush(Color.Parse("#C00E1117")).ToImmutable();
        _overlay.Child = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(28, 22),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#171B23")).ToImmutable(),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A303B")).ToImmutable(),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Connection lost", FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    _overlayReason,
                    _overlayCountdown,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Children = { now, cancel } },
                },
            },
        };
        return _overlay;
    }

    /// <summary>Shows the notice over the session and calls <paramref name="reconnect"/> after <paramref name="delay"/>, unless cancelled.</summary>
    public void ShowReconnecting(int attempt, int attempts, TimeSpan delay, Action reconnect)
    {
        _reconnectNow = reconnect;
        _reconnectAt = DateTime.UtcNow + delay;
        _overlayReason.Text = LastMessage;
        _overlayReason.IsVisible = !string.IsNullOrEmpty(LastMessage);
        _attemptText = $"(try {attempt} of {attempts})";
        _overlay.IsVisible = true;
        CountDown();
        _countdown.Start();
    }

    public void CancelReconnect()
    {
        _countdown.Stop();
        _reconnectNow = null;
        _overlay.IsVisible = false;
    }

    void CountDown()
    {
        var left = (int)Math.Ceiling((_reconnectAt - DateTime.UtcNow).TotalSeconds);
        if (left <= 0)
        {
            Fire();
            return;
        }

        _overlayCountdown.Text = $"Reconnecting in {left} s {_attemptText}";
    }

    void Fire()
    {
        var reconnect = _reconnectNow;
        CancelReconnect();
        reconnect?.Invoke();
    }

    public event EventHandler? Selected;
    public event EventHandler? CloseRequested;

    /// <summary>Raised on the UI thread when the session connects, ends or fails.</summary>
    public event EventHandler? StateChanged;

    /// <summary>user@host, plus the port when it isn't the protocol's usual one.</summary>
    public string Address
    {
        get
        {
            var target = Session.Target;
            var user = string.IsNullOrEmpty(target.Username) ? string.Empty : $"{target.Username}@";
            var port = target.Port == ProtocolInfo.DefaultPort(Origin.Protocol) ? string.Empty : $":{target.Port}";
            return $"{user}{target.Host}{port}";
        }
    }

    public bool IsSelected
    {
        set
        {
            Header.Classes.Set("selected", value);
            Header.IsCurrent = value;
            _accent.IsVisible = value;
            Host.IsVisible = value;
        }
    }

    void ShowState()
    {
        _dot.Fill = Session.State switch
        {
            SessionState.Connected => ConnectedBrush,
            SessionState.Connecting => ConnectingBrush,
            SessionState.Failed => FailedBrush,
            _ => EndedBrush,
        };

        // The dot's colour, in words.
        Header.Status = Session.State switch
        {
            SessionState.Connected => "Connected",
            SessionState.Connecting => "Connecting",
            SessionState.Failed => "Failed",
            _ => "Ended",
        };
    }
}

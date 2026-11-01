using Avalonia;
using Avalonia.Controls;
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

    /// <summary>A shell on this computer rather than a connection; <see cref="Protocol"/> only picks its colours.</summary>
    public bool Local { get; init; }

    /// <summary>A local terminal's shell, by id ("pwsh", "wsl:Ubuntu"), so it can be opened again the same way.</summary>
    public string? LocalShell { get; init; }

    /// <summary>What the tab and toolbar call it: the protocol, SFTP or LOCAL.</summary>
    public string Label => Local ? "LOCAL" : Files ? "SFTP" : ProtocolInfo.Label(Protocol);
}

/// <summary>
/// One open session: its view, and its header in the tab strip with a status dot, the protocol and a
/// close button. The header raises <see cref="Selected"/> and <see cref="CloseRequested"/>.
/// </summary>
sealed class SessionTab
{

    readonly StateGlyph _state = new() { VerticalAlignment = VerticalAlignment.Center };
    // The current tab's top edge: drawn over the tab's own outline (hence the -1 margin) so it
    // follows the rounded corners instead of sitting inside them.
    readonly Border _accent = new()
    {
        Margin = new Thickness(-1, -1, -1, 0),
        BorderThickness = new Thickness(0, 2, 0, 0),
        CornerRadius = new CornerRadius(8, 8, 0, 0),
        BorderBrush = Palette.Accent,
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
            Text = origin.Label,
            FontFamily = Dialogs.MonoFont,
            FontSize = 10,
            Foreground = ProtocolStyle.Foreground(origin.Protocol),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var close = new Button { Content = StrokeIcon.Create(Icons.Close, 12), VerticalAlignment = VerticalAlignment.Center };
        close.Classes.Add("icon");
        close.Classes.Add("tabclose");
        ToolTip.SetTip(close, origin.Local ? "End the shell and close" : "Disconnect and close");
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
                        Children = { _state, title, protocol, close },
                    },
                },
            },
        };
        Header.Classes.Add("tab");
        Header.Label = $"{origin.Title}, {(origin.Local ? "local terminal" : protocol.Text)}";
        Avalonia.Automation.AutomationProperties.SetHelpText(Header, Address);
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
            if (e.State == SessionState.Failed)
            {
                Failure = ConnectionFailure.Classify(e.Error, LastMessage, Session.Target, NetworkWatch.IsAvailable());
                FailedAt = DateTimeOffset.Now;
            }

            ShowState();
            ShowFailure();
            StateChanged?.Invoke(this, EventArgs.Empty);
        });

        // What a screen reader says when the focus lands in the session.
        if (string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(view)))
        {
            Avalonia.Automation.AutomationProperties.SetName(view, ViewName(view, origin.Title));
        }

        if (view is TerminalView or RemoteFrameView && string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetHelpText(view)))
        {
            Avalonia.Automation.AutomationProperties.SetHelpText(view, Input.SessionEscape.Help);
        }

        _countdown.Tick += (_, _) => CountDown();
        Host = new Grid { Children = { view, BuildScreen() } };
        ShowState();
    }

    internal static string ViewName(Control view, string title) => view switch
    {
        TerminalView => $"Terminal: {title}",
        RemoteFrameView => $"Remote desktop: {title}",
        FileBrowserView => $"Files: {title}",
        _ => title,
    };

    public IConnectionSession Session { get; }
    public Control View { get; }
    public SessionOrigin Origin { get; }
    public SessionTabHeader Header { get; }

    /// <summary>The view with the connection-lost screen over it; what goes in the session area.</summary>
    public Control Host { get; }

    /// <summary>Whether this session got as far as connected, as opposed to failing on the way.</summary>
    public bool HasConnected { get; private set; }

    /// <summary>Which automatic try this session is: 0 for one the user opened.</summary>
    public int ReconnectAttempt { get; init; }

    /// <summary>Set when the user disconnected or closed it, so its ending isn't treated as a drop.</summary>
    public bool EndedByUser { get; set; }

    /// <summary>Why it last failed, from the session.</summary>
    public string? LastMessage { get; private set; }

    /// <summary>What went wrong, once it has failed.</summary>
    public ConnectionFailure? Failure { get; private set; }

    /// <summary>When it failed.</summary>
    public DateTimeOffset? FailedAt { get; private set; }

    /// <summary>The user pressed Stop, so it isn't tried again by itself, not even when the network comes back.</summary>
    public bool RetryStopped { get; private set; }

    /// <summary>Counting down to an automatic try.</summary>
    public bool IsReconnecting => _reconnectNow is not null;

    /// <summary>The connection-lost screen is up.</summary>
    public bool IsShowingFailure => _screen.IsVisible;

    /// <summary>
    /// Whether this may reconnect by itself: it can (its connection has reconnecting on), it failed, the user didn't
    /// end it or press Stop, and the failure allows it: a drop (or an automatic try failing) or a network error,
    /// never a refused login, a host key or a certificate. A logoff ends as Disconnected and isn't retried.
    /// </summary>
    public bool CanRetryByItself =>
        Session.State == SessionState.Failed && Origin.Restart is not null && !EndedByUser && !RetryStopped
        && Failure is { } failure && failure.CanRetryByItself(Dropped);

    /// <summary>It failed after connecting, or while an automatic try after a failure was connecting.</summary>
    bool Dropped => HasConnected || ReconnectAttempt > 0;

    /// <summary>The user pressed Reconnect on the connection-lost screen.</summary>
    public event EventHandler? ReconnectRequested;

    // ---- Connection-lost screen ----

    readonly Panel _screen = new() { IsVisible = false };
    readonly Border _icon = new() { HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _heading = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _summary = Centred(new TextBlock());
    readonly TextBlock _advice = Themed(Centred(new TextBlock()), TextBlock.ForegroundProperty, "OrreryMuted");
    readonly TextBlock _retryText = Themed(new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, "OrrerySoftText");
    readonly Button _stop = new() { Content = "Stop" };
    readonly StackPanel _retryRow = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
    readonly Button _reconnect = new() { Content = "Reconnect" };
    readonly TextBlock _when = Themed(new TextBlock { FontSize = 12 }, TextBlock.ForegroundProperty, "OrreryMuted");
    readonly SelectableTextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = Dialogs.MonoFont };
    readonly Expander _details = new() { Header = "Details", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _reconnectAt;
    string _attemptText = string.Empty;
    Action? _reconnectNow;

    Control BuildScreen()
    {
        _reconnect.Classes.Add("accent");
        if (Origin.Local)
        {
            // Nothing to reconnect to: the button starts the shell again, as the toolbar's Restart does.
            _reconnect.Content = "Restart";
        }

        _reconnect.Click += (_, _) => ReconnectRequested?.Invoke(this, EventArgs.Empty);
        _stop.Click += (_, _) => StopReconnecting();
        var copy = new Button { Content = "Copy details" };
        copy.Click += async (_, _) => await CopyDetailsAsync();
        _retryRow.Children.Add(_retryText);
        _retryRow.Children.Add(_stop);
        _details.Content = new StackPanel { Spacing = 6, Children = { _when, _detail } };

        // The session stays faintly visible behind, so it's clear which one this is about.
        var backdrop = Themed(new Border { Opacity = 0.85 }, Border.BackgroundProperty, "OrreryBackground");
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 460,
            Padding = new Thickness(28, 22),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    _icon,
                    _heading,
                    _summary,
                    _advice,
                    _retryRow,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Children = { _reconnect, copy } },
                    _details,
                },
            },
        };
        Themed(card, Border.BackgroundProperty, "OrreryRaised");
        Themed(card, Border.BorderBrushProperty, "OrreryControlBorder");
        _screen.Children.Add(backdrop);
        _screen.Children.Add(card);
        return _screen;
    }

    static TextBlock Centred(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return text;
    }

    /// <summary>Follows a palette brush, so a different palette (colour-blind safe, say) reaches the screen too.</summary>
    static T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    /// <summary>
    /// The three kinds of failure the screen tells apart, each by its icon and its words and never by colour alone:
    /// one Orrery can try again, one that needs the user, and anything else.
    /// </summary>
    internal enum Look
    {
        Retry,
        NeedsYou,
        Other,
    }

    /// <summary>The icon on the screen, as its path data.</summary>
    internal string? IconShown { get; private set; }

    internal Look Shown => Failure switch
    {
        { NeedsUser: true } => Look.NeedsYou,
        { } failure when failure.CanRetryByItself(Dropped) => Look.Retry,
        _ => Look.Other,
    };

    /// <summary>Shows the connection-lost screen while the session has failed, and hides it otherwise.</summary>
    void ShowFailure()
    {
        bool show = Session.State == SessionState.Failed && Failure is not null && !EndedByUser;
        if (!show)
        {
            CancelReconnect();
            _screen.IsVisible = false;
            return;
        }

        var failure = Failure!;
        var (icon, brush) = Shown switch
        {
            Look.Retry => (Icons.Refresh, "OrreryAccent"),
            Look.NeedsYou => (Icons.Shield, "OrreryDanger"),
            _ => (Icons.Alert, "OrreryMuted"),
        };
        IconShown = icon;
        _icon.Child = Themed(StrokeIcon.Create(icon, 30), StrokeIcon.ForegroundProperty, brush);
        _heading.Text = Heading(failure);
        _summary.Text = failure.Summary;
        _advice.Text = failure.Advice;
        _advice.IsVisible = !string.IsNullOrEmpty(failure.Advice);
        _when.Text = $"At {FailedAt:HH:mm:ss} on {FailedAt:d MMMM yyyy}";
        _detail.Text = failure.Detail;

        // Says in words why there's no countdown; a countdown, when there is one, replaces it.
        _stop.IsVisible = false;
        _retryText.Text = failure.NeedsUser ? "This needs you, so Orrery won't try again by itself."
            : failure.CanRetryByItself(Dropped) && Origin.Restart is null ? "Reconnecting by itself is off for this connection."
            : null;
        _retryRow.IsVisible = _retryText.Text is not null;
        _screen.IsVisible = true;

        // The focus lands on Reconnect, so a screen reader hears what happened along with the button.
        var spoken = $"{_heading.Text}. {failure.Summary}";
        Avalonia.Automation.AutomationProperties.SetHelpText(_reconnect, spoken);
        if (Host.IsVisible)
        {
            ScreenReader.Announce(_screen, spoken, "connection-lost", ScreenReader.MostRecent);
            Dispatcher.UIThread.Post(FocusContent, DispatcherPriority.Background);
        }
    }

    string Heading(ConnectionFailure failure) => failure.Cause switch
    {
        _ when Origin.Local => Dropped ? "The shell stopped" : "Couldn't start the shell",
        FailureKind.LoginFailed => "Login refused",
        FailureKind.KeyFile => "Can't use the key file",
        FailureKind.HostKeyChanged => "Host key changed",
        FailureKind.HostKeyNotTrusted => "Host key not trusted",
        FailureKind.CertificateNotTrusted => "Certificate not trusted",
        _ => Dropped ? "Connection lost" : "Couldn't connect",
    };

    /// <summary>Focuses what the user is most likely to want: Reconnect when the session has failed, else the session.</summary>
    public void FocusContent()
    {
        // A prompt over the window (a host key to verify, say) keeps the focus; the session gets it when the prompt closes.
        if (TopLevel.GetTopLevel(View) is Window { OwnedWindows.Count: > 0 })
        {
            return;
        }

        if (_screen.IsVisible)
        {
            _reconnect.Focus();
        }
        else
        {
            View.Focus();
        }
    }

    /// <summary>What Copy details puts on the clipboard: what happened, to what, when, and the technical messages.</summary>
    public string DetailsText
    {
        get
        {
            if (Failure is not { } failure)
            {
                return string.Empty;
            }

            var what = Origin.Local ? $"local terminal on {Address}" : $"{Origin.Label} to {Address}";
            var lines = new List<string> { $"{Heading(failure)}: \"{Origin.Title}\", {what}", failure.Summary };
            if (failure.Advice is { } advice)
            {
                lines.Add(advice);
            }

            lines.Add($"At {FailedAt:yyyy-MM-dd HH:mm:ss zzz}");
            lines.Add(string.Empty);
            lines.Add(failure.Detail);
            return string.Join(Environment.NewLine, lines);
        }
    }

    async Task CopyDetailsAsync()
    {
        if (TopLevel.GetTopLevel(_screen)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(DetailsText);
        }
    }

    /// <summary>Counts down on the screen and calls <paramref name="reconnect"/> after <paramref name="delay"/>, unless stopped.</summary>
    public void ShowReconnecting(int attempt, int attempts, TimeSpan delay, Action reconnect)
    {
        _reconnectNow = reconnect;
        _reconnectAt = DateTime.UtcNow + delay;
        _attemptText = $"(try {attempt} of {attempts})";
        _stop.IsVisible = _retryRow.IsVisible = true;
        CountDown();
        _countdown.Start();
    }

    /// <summary>Says the automatic tries have run out. Reconnect still works, and so does the network coming back.</summary>
    public void ShowGaveUp(int attempts)
    {
        CancelReconnect();
        _retryText.Text = $"Stopped trying after {attempts} tries.";
        _retryRow.IsVisible = true;
    }

    /// <summary>
    /// The network is back: an automatic try that's waiting goes in a moment instead. True when one was waiting.
    /// </summary>
    public bool RetrySoon(TimeSpan within)
    {
        if (!IsReconnecting)
        {
            return false;
        }

        var soon = DateTime.UtcNow + within;
        if (soon < _reconnectAt)
        {
            _reconnectAt = soon;
            CountDown();
        }

        return true;
    }

    /// <summary>The user pressed Stop: no more automatic tries for this session.</summary>
    public void StopReconnecting()
    {
        RetryStopped = true;
        CancelReconnect();
        _retryText.Text = "Stopped. Press Reconnect to try again.";
        _retryRow.IsVisible = true;
    }

    public void CancelReconnect()
    {
        _countdown.Stop();
        _reconnectNow = null;
        _stop.IsVisible = false;
        _retryRow.IsVisible = false;
    }

    void CountDown()
    {
        var left = (int)Math.Ceiling((_reconnectAt - DateTime.UtcNow).TotalSeconds);
        if (left <= 0)
        {
            Fire();
            return;
        }

        _retryText.Text = $"Trying again in {left} s {_attemptText}";
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

    /// <summary>user@host, plus the port when it isn't the protocol's usual one; user@computer for a local shell.</summary>
    public string Address
    {
        get
        {
            var target = Session.Target;
            if (Origin.Local)
            {
                return $"{target.Username}@{target.Host}";
            }

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

    /// <summary>The state in words, as the tooltip and screen readers give it.</summary>
    public string StateText => StateGlyph.Describe(Session.State);

    // The mark's shape and the tooltip say the state too, so it never rests on the colour alone.
    void ShowState()
    {
        _state.State = Session.State;
        ToolTip.SetTip(Header, $"{Address} · {StateText}");

        // A screen reader hears the tab's name ("web01, SSH") and this as its status.
        Header.Status = StateText;
    }
}

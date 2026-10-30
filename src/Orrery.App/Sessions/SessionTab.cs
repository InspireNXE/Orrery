using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

/// <summary>Where a session came from: its tab title, and how to open it again after it ends.</summary>
sealed record SessionOrigin(string Title, Protocol Protocol, Guid? EntryId, Func<Task> Reconnect);

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
            Text = ProtocolInfo.Label(origin.Protocol),
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

        Header = new Button
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
        ToolTip.SetTip(Header, Address);
        Header.Click += (_, _) => Selected?.Invoke(this, EventArgs.Empty);

        session.StateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ShowState();
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
        ShowState();
    }

    public IConnectionSession Session { get; }
    public Control View { get; }
    public SessionOrigin Origin { get; }
    public Button Header { get; }

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
            _accent.IsVisible = value;
            View.IsVisible = value;
        }
    }

    void ShowState() => _dot.Fill = Session.State switch
    {
        SessionState.Connected => ConnectedBrush,
        SessionState.Connecting => ConnectingBrush,
        SessionState.Failed => FailedBrush,
        _ => EndedBrush,
    };
}

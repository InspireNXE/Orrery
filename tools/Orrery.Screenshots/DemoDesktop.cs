using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Orrery.Core.Sessions;

namespace Orrery.Screenshots;

/// <summary>
/// A remote desktop that shows a made-up Windows desktop drawn by Avalonia: no RDP, no network. It resizes to whatever
/// the view asks for, as an RDP server with display control does, and keeps link stats the tool fills in.
/// </summary>
sealed class DemoDesktop(string host) : IFrameBufferSession, ILinkStatsSession, IDisplayScaleSession
{
    byte[] _pixels = [];

    public ConnectionTarget Target { get; } = new(host, 3389, "demo", null);
    public SessionState State { get; private set; } = SessionState.Connecting;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public LinkStats Link { get; } = new();
    public bool FollowsDisplayScale => true;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;

    /// <summary>Connects at <paramref name="width"/> × <paramref name="height"/>, with the first picture.</summary>
    public void Connect(int width, int height)
    {
        Draw(width, height);
        State = SessionState.Connected;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(SessionState.Connected, null));
        FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
    }

    public void RequestResize(int width, int height)
    {
        if (State != SessionState.Connected || width <= 0 || height <= 0 || (width == Width && height == Height))
        {
            return;
        }

        Draw(width, height);
        FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
    }

    public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride)
    {
        for (int y = region.Y; y < region.Bottom; y++)
        {
            Marshal.Copy(_pixels, (y * Width + region.X) * 4, destination + y * destinationStride + region.X * 4, region.Width * 4);
        }
    }

    public void SetDisplayScale(double renderScaling) { }
    public void SendKey(ushort scancode, bool pressed) { }
    public void ReleaseAllKeys() { }
    public void SendPointerMove(int x, int y) { }
    public void SendPointerButton(PointerButton button, bool pressed) { }
    public void SendWheel(int units, bool horizontal) { }
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Draws the desktop at this size and keeps it as RGBA, which is what the view copies.</summary>
    void Draw(int width, int height)
    {
        var desktop = Scene();
        desktop.Measure(new Size(width, height));
        desktop.Arrange(new Rect(0, 0, width, height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(desktop);
        var bgra = new byte[width * height * 4];
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bgra.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }

        for (int i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            bgra[i + 3] = 255;
        }

        (_pixels, Width, Height) = (bgra, width, height);
    }

    static readonly IBrush Wallpaper = new SolidColorBrush(Color.Parse("#14344F"));
    static readonly IBrush Taskbar = new SolidColorBrush(Color.Parse("#1B1F26"));
    static readonly IBrush WindowBack = new SolidColorBrush(Color.Parse("#F3F4F6"));
    static readonly IBrush TitleBar = new SolidColorBrush(Color.Parse("#E4E7EB"));
    static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#1F2933"));
    static readonly IBrush Soft = new SolidColorBrush(Color.Parse("#616E7C"));
    static readonly IBrush Rule = new SolidColorBrush(Color.Parse("#D2D6DC"));
    static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#2F855A"));
    static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#B7791F"));

    /// <summary>A desktop with one window: an app server's service list, and a taskbar with the clock.</summary>
    static Control Scene()
    {
        (string Name, string State, IBrush Brush, string Started)[] services =
        [
            ("Inventory API", "Running", Ok, "Oct 29, 08:14"),
            ("Order queue worker", "Running", Ok, "Oct 29, 08:14"),
            ("Report scheduler", "Running", Ok, "Oct 30, 22:00"),
            ("Search indexer", "Paused", Warn, "Oct 31, 10:02"),
            ("Mail relay", "Running", Ok, "Oct 29, 08:15"),
            ("Backup agent", "Running", Ok, "Oct 29, 08:15"),
        ];

        var rows = new StackPanel();
        rows.Children.Add(Row("Service", "Status", null, "Started", header: true));
        foreach (var (name, state, brush, started) in services)
        {
            rows.Children.Add(Row(name, state, brush, started, header: false));
        }

        var window = new Border
        {
            Width = 520,
            Background = WindowBack,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Rule,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(32, 32, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        Background = TitleBar,
                        Padding = new Thickness(14, 9),
                        Child = new TextBlock { Text = "App services — app-03", FontSize = 13, Foreground = Ink },
                    },
                    new StackPanel
                    {
                        Margin = new Thickness(18, 14, 18, 18),
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Services", FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ink },
                            new TextBlock { Text = "6 services · 5 running · 1 paused", FontSize = 12, Foreground = Soft },
                            rows,
                        },
                    },
                },
            },
        };

        var taskbar = new Border
        {
            Height = 40,
            Background = Taskbar,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new DockPanel
            {
                Margin = new Thickness(14, 0),
                Children =
                {
                    Docked(new TextBlock { Text = "10:42", FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }, Dock.Right),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { Tile("#4C8DFF"), Tile("#8A94A6"), Tile("#8A94A6"), Tile("#8A94A6") },
                    },
                },
            },
        };

        return new Grid { Background = Wallpaper, Children = { window, taskbar } };
    }

    static Control Row(string name, string state, IBrush? brush, string started, bool header)
    {
        var weight = header ? FontWeight.SemiBold : FontWeight.Normal;
        var color = header ? Soft : Ink;
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (brush is not null)
        {
            status.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = brush, VerticalAlignment = VerticalAlignment.Center });
        }

        status.Children.Add(new TextBlock { Text = state, FontSize = 12, FontWeight = weight, Foreground = color });
        var name1 = new TextBlock { Text = name, FontSize = 12, FontWeight = weight, Foreground = color };
        var when = new TextBlock { Text = started, FontSize = 12, FontWeight = weight, Foreground = color };
        Grid.SetColumn(status, 1);
        Grid.SetColumn(when, 2);
        return new Border
        {
            BorderBrush = Rule,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 7),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,*"), Children = { name1, status, when } },
        };
    }

    static Control Tile(string color) => new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.Parse(color)) };

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}

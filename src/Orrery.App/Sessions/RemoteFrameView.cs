using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Orrery.App.Input;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

/// <summary>
/// Shows an <see cref="IFrameBufferSession"/> (RDP today, VNC later) and forwards keyboard and pointer
/// input to it. Frame updates arrive on a background thread and are coalesced into one copy per UI frame.
/// </summary>
public sealed class RemoteFrameView : Panel
{
    static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#0B0D10"));
    static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9AA3B2"));
    static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF9AA2"));

    readonly Image _image = new()
    {
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    readonly TextBlock _status = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 480,
        FontSize = 14,
    };

    const int WheelDelta = 120;
    static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(400);

    readonly object _dirtyLock = new();
    readonly DispatcherTimer _resizeTimer;
    FrameRect _dirty;
    bool _resizePending;
    bool _renderQueued;
    WriteableBitmap? _bitmap;
    TopLevel? _topLevel;

    readonly bool _zoom;

    // Remote pixels per DIP. Unzoomed, the remote desktop is sized in device pixels and drawn at that DPI,
    // so it stays sharp but its text is as small as at 100%. Zoomed, it is sized in DIPs and enlarged to fit,
    // because IronRDP's .NET API can't tell the server the display scale. Pointer positions use the same factor.
    double _scale = 1;
    double _wheelX, _wheelY;

    /// <param name="zoom">Enlarge the remote desktop to match this display's scaling.</param>
    public RemoteFrameView(IFrameBufferSession session, bool zoom = false)
    {
        Session = session;
        _zoom = zoom;
        Focusable = true;
        ClipToBounds = true;
        Background = SurfaceBrush;
        Children.Add(_image);
        Children.Add(_status);

        // Window drags fire many size changes; ask the server once the size settles.
        _resizeTimer = new DispatcherTimer { Interval = ResizeSettleDelay };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            var (width, height) = DesktopSize(Bounds.Size, _topLevel?.RenderScaling ?? 1, _zoom);
            Session.RequestResize(width, height);
        };

        session.FrameUpdated += OnFrameUpdated;
        session.StateChanged += (_, e) => Dispatcher.UIThread.Post(() => ShowState(e.State, e.Message));
        ShowState(session.State, null);
    }

    public IFrameBufferSession Session { get; }

    void ShowState(SessionState state, string? message)
    {
        _status.IsVisible = state != SessionState.Connected;
        _status.Foreground = state == SessionState.Failed ? DangerBrush : MutedBrush;
        _status.Text = state switch
        {
            SessionState.Connecting => $"Connecting to {Session.Target.Host}…",
            SessionState.Disconnected => "Disconnected.",
            SessionState.Failed => $"Couldn't connect: {message}",
            _ => string.Empty,
        };

        if (state == SessionState.Connected)
        {
            Focus();
            ScheduleResize(); // The view may have changed size while connecting.
        }
    }

    void ScheduleResize()
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ScheduleResize();
    }

    void OnFrameUpdated(object? sender, FrameUpdatedEventArgs e)
    {
        lock (_dirtyLock)
        {
            _dirty = _dirty.Union(e.Damage);
            _resizePending |= e.Resized;
            if (_renderQueued)
            {
                return;
            }

            _renderQueued = true;
        }

        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
    }

    void Flush()
    {
        FrameRect dirty;
        bool resize;
        lock (_dirtyLock)
        {
            (dirty, resize) = (_dirty, _resizePending);
            (_dirty, _resizePending, _renderQueued) = (default, false, false);
        }

        int width = Session.Width, height = Session.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var dpi = new Vector(96 * _scale, 96 * _scale);
        if (resize || _bitmap is null || _bitmap.PixelSize != new PixelSize(width, height) || _bitmap.Dpi != dpi)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), dpi, PixelFormat.Rgba8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
            dirty = new FrameRect(0, 0, width, height);
        }

        using (var buffer = _bitmap.Lock())
        {
            Session.CopyRegion(dirty, buffer.Address, buffer.RowBytes);
        }

        _image.InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Swallow every key so Tab, arrows and shortcuts go to the remote machine, not Avalonia focus.
        e.Handled = true;
        if (ScancodeMap.Get(e.PhysicalKey) is { } code)
        {
            Session.SendKey(code, pressed: true);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        if (ScancodeMap.Get(e.PhysicalKey) is { } code)
        {
            Session.SendKey(code, pressed: false);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(_image) * _scale;
        Session.SendPointerMove((int)p.X, (int)p.Y);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;

        // Avalonia reports notches (fractions from precision touchpads); RDP wants 120 per notch.
        // Keep the remainder so slow touchpad scrolling still adds up.
        // Avalonia's horizontal delta runs opposite to RDP's. Check this on macOS and Linux, where
        // trackpad direction settings may also apply.
        _wheelY += e.Delta.Y * WheelDelta;
        _wheelX -= e.Delta.X * WheelDelta;

        int vertical = (int)_wheelY, horizontal = (int)_wheelX;
        _wheelY -= vertical;
        _wheelX -= horizontal;
        Session.SendWheel(vertical, horizontal: false);
        Session.SendWheel(horizontal, horizontal: true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (ToButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind) is { } button)
        {
            Session.SendPointerButton(button, pressed: true);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (ToButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind) is { } button)
        {
            Session.SendPointerButton(button, pressed: false);
            e.Handled = true;
        }
    }

    static PointerButton? ToButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => PointerButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => PointerButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => PointerButton.Middle,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => PointerButton.X1,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => PointerButton.X2,
        _ => null,
    };

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        Session.ReleaseAllKeys();
    }

    // Switching windows keeps this control focused inside its window, so OnLostFocus doesn't fire.
    void OnWindowDeactivated(object? sender, EventArgs e) => Session.ReleaseAllKeys();

    /// <summary>The window moved to a monitor with different scaling.</summary>
    void OnScalingChanged(object? sender, EventArgs e)
    {
        UpdateScale();
        ScheduleResize();
    }

    void UpdateScale()
    {
        double displayScale = _topLevel?.RenderScaling ?? 1;
        _scale = _zoom ? 1 : displayScale;

        // Whole-number zoom (200%) stays crisp with plain pixel doubling; other factors need smoothing.
        bool wholeZoom = Math.Abs(displayScale - Math.Round(displayScale)) < 0.01;
        RenderOptions.SetBitmapInterpolationMode(_image,
            !_zoom || displayScale <= 1 ? BitmapInterpolationMode.Unspecified
            : wholeZoom ? BitmapInterpolationMode.None
            : BitmapInterpolationMode.HighQuality);
    }

    /// <summary>The desktop size to ask for, in remote pixels, for a session area of <paramref name="size"/> DIPs.</summary>
    public static (int Width, int Height) DesktopSize(Size size, double displayScale, bool zoom)
    {
        double scale = zoom ? 1 : displayScale;
        return ((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            UpdateScale();
            _topLevel.ScalingChanged += OnScalingChanged;
            if (_topLevel is WindowBase window)
            {
                window.Deactivated += OnWindowDeactivated;
            }
        }

        // TabControl detaches hidden tabs; repaint everything when this one comes back.
        if (Session.State == SessionState.Connected)
        {
            OnFrameUpdated(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Session.Width, Session.Height), resized: true));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _resizeTimer.Stop();
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            if (_topLevel is WindowBase window)
            {
                window.Deactivated -= OnWindowDeactivated;
            }

            _topLevel = null;
        }

        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    readonly object _dirtyLock = new();
    FrameRect _dirty;
    bool _resizePending;
    bool _renderQueued;
    WriteableBitmap? _bitmap;

    public RemoteFrameView(IFrameBufferSession session)
    {
        Session = session;
        Focusable = true;
        ClipToBounds = true;
        Background = SurfaceBrush;
        Children.Add(_image);
        Children.Add(_status);

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
        }
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

        if (resize || _bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Opaque);
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
        var p = e.GetPosition(_image);
        Session.SendPointerMove((int)p.X, (int)p.Y);
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // TabControl detaches hidden tabs; repaint everything when this one comes back.
        if (Session.State == SessionState.Connected)
        {
            OnFrameUpdated(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Session.Width, Session.Height), resized: true));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

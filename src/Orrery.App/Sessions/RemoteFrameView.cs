using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.App.Controls;
using Orrery.App.Input;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;

namespace Orrery.App.Sessions;

/// <summary>
/// Shows an <see cref="IFrameBufferSession"/> (RDP today, VNC later) and forwards keyboard and pointer
/// input to it. Frame updates arrive on a background thread and are coalesced into one copy per UI frame.
/// </summary>
public sealed partial class RemoteFrameView : Panel
{
    static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#0B0D10"));

    // A desktop the server won't resize (most VNC servers) shrinks to fit; one that matches the view is
    // drawn as is. See Scaling for the other ways.
    readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Scrolls only at actual size; otherwise its bars are off, so the image is given the view's size to fit into.
    readonly ScrollViewer _scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    // Fingers on the screen and pens in range that go to the session as themselves (ITouchInput), by pointer.
    readonly HashSet<int> _touches = [];
    readonly HashSet<int> _pensInRange = [];

    // The mouse buttons the remote has been told are down.
    readonly HashSet<PointerButton> _buttonsDown = [];

    // The End key of a Ctrl+Alt+End that was sent as Ctrl+Alt+Del, so its release isn't sent too.
    PhysicalKey? _ctrlAltDelKey;

    // VNC takes keysyms; the keysym sent on key down is remembered so the release matches it, even if
    // Shift changed what the key types in between.
    readonly Dictionary<PhysicalKey, uint> _keysymsDown = [];
    readonly Action<HookedKey, bool> _windowsKey;

    // The clipboard text last exchanged with the server (VNC, or RDP off Windows), so a change isn't sent back where it came from.
    string? _lastClipboard;

    // Over the last picture while not connected, so a dropped desktop doesn't look like a frozen live one.
    readonly Border _dim = new()
    {
        Background = new SolidColorBrush(Color.Parse("#0B0D10"), 0.72),
        IsHitTestVisible = false,
        IsVisible = false,
    };

    readonly Notice _status = new(NoticeKind.Info)
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 480,
    };

    // Something to tell the user while they're looking at the desktop, such as clipboard text that wasn't shared: across
    // the top, where it's seen, not in the status bar.
    readonly Notice _bannerNotice = new(NoticeKind.Warning) { VerticalAlignment = VerticalAlignment.Center };
    readonly Border _banner;
    static readonly TimeSpan BannerTime = TimeSpan.FromSeconds(8);
    int _bannerVersion;

    const int WheelDelta = 120;
    // A window drag fires many size changes: the server is asked once they settle, and at most this often while they
    // go on. Measured on a LAN, the server resized in 8 ms and drew in 40, so waiting longer was most of the delay.
    // The first change after a quiet spell (a maximize, full screen, a pane opening, or a drag starting) is asked for
    // sooner, with the size the view has by then: waiting for those to settle was most of their delay too.
    internal static readonly TimeSpan ResizeFirstDelay = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan ResizeMaxWait = TimeSpan.FromMilliseconds(300);

    // How long the old picture stays stretched to the view if the server never sends the new size.
    static readonly TimeSpan StretchGiveUp = TimeSpan.FromSeconds(2);

    long _resizeWantedSince;
    long _lastSizeChange;
    bool _firstAskComing;
    readonly DispatcherTimer _stretchTimer;

    // Whether the server is known to follow the view's size: RDP does; another learns it from the first resize.
    bool _serverFollows;
    bool _stretching;
    bool _askedForSize;

    readonly object _dirtyLock = new();
    readonly DispatcherTimer _resizeTimer;
    FrameRect _dirty;
    bool _resizePending;
    bool _renderQueued;
    WriteableBitmap? _bitmap;

    // Whether the view is in a window. TabControl takes a hidden tab's view out, and then nothing is drawn for it: the
    // session keeps the picture itself, and the whole of it is drawn when the tab shows again (OnAttachedToVisualTree).
    // Before, a hidden tab made a bitmap again at the next change and kept copying every frame on the UI thread.
    volatile bool _onScreen;

    const int BytesPerPixel = 4;

    // The changed part of the frame, copied out of the session before the bitmap is locked (see Flush).
    byte[]? _scratch;

    /// <summary>True while the bitmap is locked to draw into it; the session is never asked for pixels then.</summary>
    internal bool DrawingIntoBitmap { get; private set; }
    TopLevel? _topLevel;

    // Set when a frame couldn't be drawn and the session was ended for it.
    string? _drawFailure;

    readonly bool _zoom;

    // Remote pixels per DIP. Unzoomed, the remote desktop is sized in device pixels and drawn one to one (SizeImage), so it stays
    // sharp; an RDP session that tells the server the display's scaling (IDisplayScaleSession) also gets text the
    // size of local apps' text, and otherwise it's as small as at 100%. Zoomed, it is sized in DIPs and enlarged to
    // fit, for a session that can't tell the server (VNC). Pointer positions use the same factor.
    double _scale = 1;
    double _wheelX, _wheelY;

    // The remote pointer's shape as a cursor over the picture, created while the view is shown.
    RemotePointerCursors? _cursors;

    /// <param name="zoom">Enlarge the remote desktop to match this display's scaling.</param>
    public RemoteFrameView(IFrameBufferSession session, bool zoom = false)
    {
        Session = session;
        _zoom = zoom;
        _windowsKey = OnHookedKey;
        if (session is IClipboardSession clipboardSession)
        {
            clipboardSession.ClipboardRefused += (_, reason) => Dispatcher.UIThread.Post(() =>
            {
                // Forgotten, so the next time the session gets the keyboard it's tried, and refused out loud, again:
                // the first refusal can come as the session opens, before anyone is looking.
                _lastClipboard = null;
                ShowBanner(reason);
            });
            clipboardSession.RemoteClipboardChanged += (_, text) => Dispatcher.UIThread.Post(async () =>
            {
                _lastClipboard = text;
                var write = SetClipboardText ?? (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
                    ? t => clipboard.SetTextAsync(t)
                    : null);
                if (write is null)
                {
                    return;
                }

                try
                {
                    await write(text);
                }
                catch (Exception e) when (e is COMException or InvalidOperationException)
                {
                    // Another app holds the clipboard open. A server can send cut text as often as it likes, and
                    // an exception here would end the app, so the next change tries again.
                    Log.For("RemoteDesktop").LogWarning("Couldn't write the clipboard: {Message}", e.Message);
                }
            });
        }
        Focusable = true;
        ClipToBounds = true;
        Background = SurfaceBrush;
        _scroller.Content = _image;
        Children.Add(_scroller);
        _piecesLayer = new PiecesLayer(this) { IsVisible = false };
        Children.Add(_piecesLayer);
        Children.Add(_dim);
        Children.Add(_status);

        // Caught on the way down, so at actual size the wheel scrolls the remote desktop's windows, not the view.
        AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var closeBanner = new Button { Content = StrokeIcon.Create(Icons.Close, 12), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        closeBanner.Classes.Add("icon");
        Avalonia.Automation.AutomationProperties.SetName(closeBanner, "Dismiss");
        closeBanner.Click += (_, _) => _banner!.IsVisible = false;
        _banner = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 12),
            MaxWidth = 640,
            Padding = new Thickness(14, 10, 8, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Palette.Warning,
            Background = Dialogs.PanelBrush,
            BoxShadow = BoxShadows.Parse("0 4 16 0 #80000000"),
            IsVisible = false,
            Child = new DockPanel { Children = { Pane.Docked(closeBanner, Dock.Right), _bannerNotice } },
        };
        Children.Add(_banner);
        if (session is IClipboardSession { ExchangesFiles: true } fileSession)
        {
            ShareFilesAndImages(fileSession);
        }

        _serverFollows = session is IDisplayScaleSession;
        _resizeTimer = new DispatcherTimer { Interval = ResizeSettleDelay };
        _resizeTimer.Tick += (_, _) => AskForViewSize();
        _stretchTimer = new DispatcherTimer { Interval = StretchGiveUp };
        _stretchTimer.Tick += (_, _) => StopStretching();

        session.FrameUpdated += OnFrameUpdated;
        session.StateChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            ShowState(e.State, e.Message);
            ShowRemotePointer();
        });
        if (session is IRemotePointerSession pointerSession)
        {
            pointerSession.PointerShapeChanged += (_, _) => Dispatcher.UIThread.Post(ShowRemotePointer);
        }
        ShowState(session.State, null);
    }

    public IFrameBufferSession Session { get; }

    /// <summary>Replaces the write to the local clipboard, which tests can't make fail: IClipboard can't be implemented outside Avalonia.</summary>
    internal Func<string, Task>? SetClipboardText { get; set; }

    /// <summary>What the banner across the top says while it shows, for tests.</summary>
    /// <summary>Whether the last picture is dimmed because the session isn't connected.</summary>
    internal bool Dimmed => _dim.IsVisible;

    internal string? BannerText => _banner.IsVisible ? _bannerNotice.Text : null;

    /// <summary>Says <paramref name="text"/> across the top of the desktop for a few seconds, and to the screen reader.</summary>
    internal void ShowNotice(string text) => ShowBanner(text);

    /// <summary>Shows <paramref name="text"/> across the top of the desktop for a few seconds, and has the screen reader read it.</summary>
    async void ShowBanner(string text)
    {
        int version = ++_bannerVersion;
        _bannerNotice.Text = text;
        _banner.IsVisible = true;
        ScreenReader.Announce(this, text, "Orrery.RemoteNotice", ScreenReader.MostRecent);
        await Task.Delay(BannerTime);
        if (version == _bannerVersion)
        {
            _banner.IsVisible = false;
        }
    }

    bool _viewOnly;

    /// <summary>
    /// Shows the remote screen without sending keys, clicks or the wheel. Keys then reach Orrery's own
    /// shortcuts instead.
    /// </summary>
    public bool ViewOnly
    {
        get => _viewOnly;
        set
        {
            if (value == _viewOnly)
            {
                return;
            }

            _viewOnly = value;
            ReleaseAllKeys();
            UpdateKeyCapture();
        }
    }

    /// <summary>Whether there's a picture to save yet. A hidden tab's comes from the session (<see cref="SaveScreenshot"/>).</summary>
    public bool HasFrame => _bitmap is not null || !_onScreen && Session.State == SessionState.Connected && Session.Width > 0 && Session.Height > 0;

    DesktopScaling _scaling = DesktopScaling.Fit;

    /// <summary>
    /// The server resizes the desktop to the view, so it always fits and <see cref="Scaling"/> has nothing to do but
    /// for a moment while one resizes. False until the server has followed a resize, and with a fixed desktop size.
    /// </summary>
    public bool ResizesToView => FollowsViewSize && _serverFollows && _spannedMonitors is null;

    /// <summary>Raised when <see cref="ResizesToView"/> changes.</summary>
    public event EventHandler? ResizesToViewChanged;

    /// <summary>
    /// How a desktop that doesn't match the view is fitted into it: shrunk when it's bigger (the default), shrunk or
    /// enlarged to fill it, or shown at its actual size with scroll bars. Either way it keeps its shape.
    /// </summary>
    public DesktopScaling Scaling
    {
        get => _scaling;
        set
        {
            _scaling = value;
            bool actual = value == DesktopScaling.ActualSize;
            _scroller.HorizontalScrollBarVisibility = _scroller.VerticalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            SizeImage();
        }
    }

    /// <summary>
    /// Holds the image to the desktop's size in DIPs, one remote pixel per device pixel when unzoomed: exactly at actual
    /// size, at most when shrinking to fit, and not at all when fitting the tab. The bitmap itself stays at 96 DPI,
    /// because Avalonia's Image takes a bitmap's size in DIPs as the pixels to draw, so at 120 DPI it showed only the
    /// top-left part of the desktop, enlarged, and clicks landed off.
    /// </summary>
    void SizeImage()
    {
        double width = double.NaN, height = double.NaN, maxWidth = double.PositiveInfinity, maxHeight = double.PositiveInfinity;
        _image.Stretch = _stretching ? Stretch.Fill : Stretch.Uniform;
        _image.StretchDirection = _stretching || _scaling == DesktopScaling.Fit ? StretchDirection.Both : StretchDirection.DownOnly;
        if (_bitmap is { } bitmap && _scaling != DesktopScaling.Fit && !_stretching)
        {
            double desktopWidth = bitmap.PixelSize.Width / _scale, desktopHeight = bitmap.PixelSize.Height / _scale;
            if (_scaling == DesktopScaling.ActualSize)
            {
                (width, height) = (desktopWidth, desktopHeight);
            }
            else
            {
                (maxWidth, maxHeight) = (desktopWidth, desktopHeight);
            }
        }

        (_image.Width, _image.Height, _image.MaxWidth, _image.MaxHeight) = (width, height, maxWidth, maxHeight);
    }

    ColorCorrection? _correction;

    /// <summary>
    /// The color correction for the user's color vision, from Settings; null for none. Changing it redraws the
    /// whole desktop. It's ignored where <see cref="AllowsColorCorrection"/> is off.
    /// </summary>
    internal ColorCorrection? Correction
    {
        get => _correction;
        set
        {
            if (value == _correction)
            {
                return;
            }

            _correction = value;
            if (AllowsColorCorrection)
            {
                Redraw();
            }
        }
    }

    bool _allowsCorrection = true;

    /// <summary>
    /// The connection's "Color correction" choice: off shows the remote colors unchanged whatever Settings says. The
    /// session's settings can change it while the desktop shows.
    /// </summary>
    public bool AllowsColorCorrection
    {
        get => _allowsCorrection;
        set
        {
            if (value == _allowsCorrection)
            {
                return;
            }

            _allowsCorrection = value;
            if (_correction is not null)
            {
                Redraw();
            }
        }
    }

    /// <summary>Draws the whole desktop again, for a change in how its colors are corrected.</summary>
    void Redraw()
    {
        if (Session.State == SessionState.Connected)
        {
            OnFrameUpdated(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Session.Width, Session.Height), resized: false));
        }
    }

    /// <summary>The correction frames are drawn with: none where the connection turns it off.</summary>
    internal ColorCorrection? ActiveCorrection => AllowsColorCorrection ? _correction : null;

    /// <summary>
    /// Copies <paramref name="region"/> of the remote desktop into a bitmap's pixels and corrects just that part,
    /// since the rest is corrected already.
    /// </summary>
    internal static void Draw(IFrameBufferSession session, ColorCorrection? correction, IntPtr pixels, int rowBytes, int width, int height, FrameRect region)
    {
        byte[]? scratch = null;
        var copied = CopyOut(session, region, width, height, ref scratch);
        Blit(scratch, copied, correction, pixels, rowBytes, width, height);
    }

    /// <summary>
    /// Copies <paramref name="region"/>, clipped to the frame, out of the session into <paramref name="scratch"/>
    /// (grown as needed), its rows packed one after the other. Returns the region copied.
    /// </summary>
    static FrameRect CopyOut(IFrameBufferSession session, FrameRect region, int width, int height, ref byte[]? scratch) =>
        FrameCopy.CopyOut(session, region, width, height, ref scratch);

    /// <summary>Copies what <see cref="CopyOut"/> took into a bitmap's pixels and corrects just that part.</summary>
    static void Blit(byte[]? scratch, FrameRect region, ColorCorrection? correction, IntPtr pixels, int rowBytes, int width, int height)
    {
        if (region.IsEmpty || scratch is null)
        {
            return;
        }

        int regionRowBytes = region.Width * BytesPerPixel;
        for (int row = 0; row < region.Height; row++)
        {
            Marshal.Copy(scratch, row * regionRowBytes,
                pixels + (nint)(region.Y + row) * rowBytes + (nint)region.X * BytesPerPixel, regionRowBytes);
        }

        correction?.Apply(pixels, rowBytes, width, height, region);
    }

    /// <summary>
    /// Writes the remote screen as it is now, color correction included, as a PNG. A hidden tab has no bitmap of its
    /// own, so its picture is drawn from the session's for this.
    /// </summary>
    public void SaveScreenshot(Stream stream)
    {
        if (_bitmap is { } shown)
        {
            shown.Save(stream, PngBitmapEncoderOptions.Default);
            return;
        }

        int width = Session.Width, height = Session.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        byte[]? scratch = null;
        var copied = CopyOut(Session, new FrameRect(0, 0, width, height), width, height, ref scratch);
        using var picture = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using (var buffer = picture.Lock())
        {
            Blit(scratch, copied, ActiveCorrection, buffer.Address, buffer.RowBytes, width, height);
        }

        picture.Save(stream, PngBitmapEncoderOptions.Default);
    }

    /// <summary>Presses the keys in order on the remote computer, then lets them go in reverse.</summary>
    public void SendKeys(RemoteKeyCombo combo)
    {
        if (Session.State != SessionState.Connected)
        {
            return;
        }

        foreach (var key in combo.Keys) Send(key, pressed: true);
        foreach (var key in combo.Keys.Reverse()) Send(key, pressed: false);

        void Send(RemoteKey key, bool pressed)
        {
            if (Session is IKeysymInput keysyms)
            {
                keysyms.SendKeysym(key.Keysym, pressed);
            }
            else
            {
                Session.SendKey(key.Scancode, pressed);
            }
        }
    }

    /// <summary>The cursor over the picture: the remote pointer's shape while connected, else the arrow.</summary>
    void ShowRemotePointer()
    {
        if (_cursors is null)
        {
            return;
        }

        var shape = Session is IRemotePointerSession pointer && Session.State == SessionState.Connected ? pointer.PointerShape : null;
        _image.Cursor = _cursors.For(shape);
    }

    /// <summary>What the remote pointer was last shown as; null for a shape shown as the server sent it. For tests.</summary>
    internal PointerKind? RemotePointerKind => _cursors?.LastKind;

    void ShowState(SessionState state, string? message)
    {
        // The session reports itself disconnected once Flush has ended it; the reason stays up instead.
        if (_drawFailure is not null)
        {
            (state, message) = (SessionState.Failed, _drawFailure);
        }

        _status.IsVisible = _dim.IsVisible = state != SessionState.Connected;
        _status.Kind = state == SessionState.Failed ? NoticeKind.Error : NoticeKind.Info;
        _status.Text = state switch
        {
            SessionState.Connecting => $"Connecting to {Session.Target.Host}…",
            SessionState.Disconnected => "Disconnected.",
            SessionState.Failed => $"Couldn't connect: {message}",
            _ => string.Empty,
        };

        UpdateKeyCapture();
        if (state == SessionState.Connected)
        {
            _ = SendLocalClipboardAsync();
            SessionFocus.TakeIfFree(this);
            ScheduleResize(); // The view may have changed size while connecting.
        }
    }

    /// <summary>Ask the server to match the view's size as it changes. Off for a fixed desktop size, which is scaled to fit instead.</summary>
    public bool FollowsViewSize { get; init; } = true;

    /// <summary>Raised when the remote desktop comes in at a new size, such as after the view is resized.</summary>
    public event EventHandler? DesktopResized;

    IReadOnlyList<RemoteMonitor>? _spannedMonitors;

    /// <summary>One remote monitor and the display it's shown on, when the two don't sit alike.</summary>
    /// <param name="Window">Where it's shown, in pixels from the window's top-left corner.</param>
    /// <param name="Remote">Where it is on the remote desktop as laid out, in pixels from its top-left corner.</param>
    internal readonly record struct Piece(PixelRect Window, PixelRect Remote);

    readonly PiecesLayer _piecesLayer;
    (IReadOnlyList<Piece> Pieces, PixelSize Layout)? _pieces;

    /// <summary>
    /// While full screen covers displays with others between them: each remote monitor drawn on its own display, the
    /// remote desktop being <paramref name="layout"/> pixels with them moved together. A VNC picture of another size is
    /// fitted to the same pieces. Null draws the desktop whole again.
    /// </summary>
    internal void ShowPieces(IReadOnlyList<Piece>? pieces, PixelSize layout)
    {
        _pieces = pieces is null ? null : (pieces, layout);
        ShowPiecesOrWhole();
    }

    bool PiecesShown => _pieces is not null && _bitmap is not null;

    void ShowPiecesOrWhole()
    {
        _piecesLayer.IsVisible = PiecesShown;
        _scroller.IsVisible = !PiecesShown;
        _piecesLayer.InvalidateVisual();
    }

    /// <summary>The remote pixel under <paramref name="windowPixel"/>, when it's on one of the pieces.</summary>
    internal (int X, int Y)? PieceRemotePoint(PixelPoint windowPixel)
    {
        if (_pieces is not { } shown)
        {
            return null;
        }

        foreach (var piece in shown.Pieces)
        {
            if (piece.Window.Contains(windowPixel))
            {
                double x = piece.Remote.X + (windowPixel.X - piece.Window.X) * (double)piece.Remote.Width / piece.Window.Width;
                double y = piece.Remote.Y + (windowPixel.Y - piece.Window.Y) * (double)piece.Remote.Height / piece.Window.Height;
                return ((int)(x * Session.Width / shown.Layout.Width), (int)(y * Session.Height / shown.Layout.Height));
            }
        }

        return null;
    }

    /// <summary>Draws the remote monitors each on its display, from the same bitmap the whole desktop is drawn from.</summary>
    sealed class PiecesLayer(RemoteFrameView view) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (view._pieces is not { } shown || view._bitmap is not { } bitmap || view._topLevel is not { } top
                || this.TranslatePoint(default, top) is not { } origin)
            {
                return;
            }

            double scaling = top.RenderScaling;
            double sx = bitmap.PixelSize.Width / (double)shown.Layout.Width, sy = bitmap.PixelSize.Height / (double)shown.Layout.Height;
            foreach (var piece in shown.Pieces)
            {
                // The bitmap is at 96 DPI, so its pixels are its DIPs.
                var source = new Rect(piece.Remote.X * sx, piece.Remote.Y * sy, piece.Remote.Width * sx, piece.Remote.Height * sy);
                var target = new Rect(piece.Window.X / scaling - origin.X, piece.Window.Y / scaling - origin.Y, piece.Window.Width / scaling, piece.Window.Height / scaling);
                context.DrawImage(bitmap, source, target);
            }
        }
    }

    /// <summary>
    /// While the window covers every display: the remote desktop's monitors, one for each display, asked for instead of
    /// one desktop the size of the view. Null for one desktop.
    /// </summary>
    internal IReadOnlyList<RemoteMonitor>? SpannedMonitors
    {
        get => _spannedMonitors;
        set
        {
            _spannedMonitors = value;
            ScheduleResize();
        }
    }

    void ScheduleResize()
    {
        if (!FollowsViewSize)
        {
            return;
        }

        // Until the server's picture comes at the new size, the old one fills the view instead of leaving gaps.
        if (_serverFollows && !_stretching && _bitmap is not null && _spannedMonitors is null && _scaling != DesktopScaling.ActualSize
            && Session.State == SessionState.Connected)
        {
            _stretching = true;
            SizeImage();
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool quiet = _lastSizeChange == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(_lastSizeChange, now) >= ResizeSettleDelay;
        _lastSizeChange = now;
        if (_firstAskComing)
        {
            // It takes the view's size as it goes, this change's too.
            return;
        }

        _resizeTimer.Stop();
        if (quiet)
        {
            _firstAskComing = true;
            _resizeWantedSince = now;
            _resizeTimer.Interval = ResizeFirstDelay;
        }
        else
        {
            _resizeTimer.Interval = ResizeSettleDelay;
            if (_resizeWantedSince == 0)
            {
                _resizeWantedSince = now;
            }
            else if (System.Diagnostics.Stopwatch.GetElapsedTime(_resizeWantedSince, now) >= ResizeMaxWait)
            {
                // Still dragging: ask for the size so far, so the desktop follows instead of catching up at the end.
                AskForViewSize();
                return;
            }
        }

        _resizeTimer.Start();
    }

    void AskForViewSize()
    {
        _resizeTimer.Stop();
        _resizeWantedSince = 0;
        _firstAskComing = false;

        // Checked here too: the constructor can start the timer before FollowsViewSize is set.
        if (!FollowsViewSize)
        {
            StopStretching();
            return;
        }

        // Spread over the displays when the server takes a layout; otherwise one desktop the size of the view,
        // which covers them all, as mstsc /span does.
        (Session as IDisplayScaleSession)?.SetDisplayScale(_topLevel?.RenderScaling ?? 1);
        if (_spannedMonitors is not { } monitors || Session is not IMultiMonitorSession spread || !spread.RequestMonitorLayout(monitors))
        {
            var (width, height) = DesktopSize(Bounds.Size, _topLevel?.RenderScaling ?? 1, _zoom);
            Session.RequestResize(width, height);
            _askedForSize |= Session.State == SessionState.Connected;
            if (_stretching)
            {
                // Already that size, or about to be, within what the server rounds to (an even width), or what an RDP
                // session leaves as it is (2 pixels more than a width that's a multiple of 4).
                if (width - Session.Width is >= -1 and <= 3 && height == Session.Height)
                {
                    StopStretching();
                }
                else
                {
                    _stretchTimer.Stop();
                    _stretchTimer.Start();
                }
            }
        }
        else
        {
            StopStretching();
        }
    }

    void StopStretching()
    {
        _stretchTimer.Stop();
        if (_stretching)
        {
            _stretching = false;
            SizeImage();
        }
    }

    /// <summary>Whether the last picture is stretched to the view while the server's picture at its new size is awaited.</summary>
    internal bool Stretching => _stretching;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ScheduleResize();
    }

    void OnFrameUpdated(object? sender, FrameUpdatedEventArgs e)
    {
        if (!_onScreen)
        {
            return;
        }

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
        if (width <= 0 || height <= 0 || _drawFailure is not null || !_onScreen)
        {
            return;
        }

        try
        {
            if (resize || _bitmap is null || _bitmap.PixelSize != new PixelSize(width, height))
            {
                _bitmap?.Dispose();
                _bitmap = null;
                _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
                _image.Source = _bitmap;
                // A new size after Orrery asked for one: the server follows the view, so later resizes stretch meanwhile.
                bool followed = ResizesToView;
                _serverFollows |= _askedForSize;
                _askedForSize = false;
                if (ResizesToView != followed)
                {
                    ResizesToViewChanged?.Invoke(this, EventArgs.Empty);
                }
                if (_stretching)
                {
                    _stretching = false;
                    _stretchTimer.Stop();
                }

                SizeImage();
                ShowPiecesOrWhole();
                dirty = new FrameRect(0, 0, width, height);
                DesktopResized?.Invoke(this, EventArgs.Empty);
            }

            // The session's lock is taken before the bitmap's, never inside it. Waiting for a lock on this thread lets
            // Windows deliver a paint, which waits for the render thread, which waits for the bitmap: holding the
            // bitmap meanwhile froze the window (seen leaving full screen while the session drew).
            var copied = CopyOut(Session, dirty, width, height, ref _scratch);
            using (var buffer = _bitmap.Lock())
            {
                DrawingIntoBitmap = true;
                try
                {
                    Blit(_scratch, copied, ActiveCorrection, buffer.Address, buffer.RowBytes, width, height);
                }
                finally
                {
                    DrawingIntoBitmap = false;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or InvalidOperationException)
        {
            // Nothing above the dispatcher catches this, so letting it go would end the whole app.
            Log.For("RemoteDesktop").LogError(e, "Couldn't draw the {Width}x{Height} remote desktop", width, height);
            _image.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
            _drawFailure = $"Orrery couldn't draw the {width}x{height} remote desktop. {e.Message}";
            ShowState(SessionState.Failed, _drawFailure);
            _ = Session.DisconnectAsync();
            return;
        }

        _image.InvalidateVisual();
        if (PiecesShown)
        {
            _piecesLayer.InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_viewOnly)
        {
            return;
        }

        // Swallow every key so Tab, arrows and shortcuts go to the remote machine, not Avalonia focus.
        e.Handled = true;
        if (CtrlAltDelShortcut.Matches(e.Key, e.KeyModifiers))
        {
            // The keys held for the shortcut are let go first, so the remote side sees exactly Ctrl+Alt+Del. Held down,
            // it's sent once.
            if (_ctrlAltDelKey is null)
            {
                _ctrlAltDelKey = e.PhysicalKey;
                ReleaseAllKeys();
                SendKeys(RemoteKeyCombo.CtrlAltDel);
            }

            return;
        }

        if (Session is IKeysymInput keysyms)
        {
            // A repeat sends the keysym from the first press.
            if (!_keysymsDown.TryGetValue(e.PhysicalKey, out var keysym))
            {
                if (KeysymMap.Get(e.Key, e.PhysicalKey, e.KeySymbol) is not { } mapped)
                {
                    return;
                }

                keysym = _keysymsDown[e.PhysicalKey] = mapped;
            }

            keysyms.SendKeysym(keysym, pressed: true);
        }
        else if (ScancodeMap.Get(e.PhysicalKey) is { } code)
        {
            Session.SendKey(code, pressed: true);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_viewOnly)
        {
            return;
        }

        e.Handled = true;
        if (_ctrlAltDelKey == e.PhysicalKey)
        {
            _ctrlAltDelKey = null;
            return;
        }

        if (Session is IKeysymInput keysyms)
        {
            if (_keysymsDown.Remove(e.PhysicalKey, out var keysym))
            {
                keysyms.SendKeysym(keysym, pressed: false);
            }
        }
        else if (ScancodeMap.Get(e.PhysicalKey) is { } code)
        {
            Session.SendKey(code, pressed: false);
        }
    }

    /// <summary>Where the pointer is on the remote desktop, in its pixels; null while nothing is shown.</summary>
    (int X, int Y)? RemotePoint(PointerEventArgs e)
    {
        if (PiecesShown && _topLevel is { } top)
        {
            var at = e.GetPosition(top) * top.RenderScaling;
            return PieceRemotePoint(new PixelPoint((int)at.X, (int)at.Y));
        }

        // Scale by what's on screen, which covers zoom, a desktop fitted to the view, and one at actual size.
        var p = e.GetPosition(_image);
        double width = _image.Bounds.Width, height = _image.Bounds.Height;
        return width > 0 && height > 0 ? ((int)(p.X * Session.Width / width), (int)(p.Y * Session.Height / height)) : null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_viewOnly || SendTouchOrPen(e, PointerStage.Moved))
        {
            return;
        }

        if (RemotePoint(e) is var (x, y))
        {
            LastRemotePointer = (x, y);
            Session.SendPointerMove(x, y);
        }

        // While a button is held, Avalonia reports another button going down or up as a move, not a press or release.
        SendButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
    }

    /// <summary>Where the pointer last was on the remote desktop, in its pixels, for a recording; null before it's moved there.</summary>
    public (int X, int Y)? LastRemotePointer { get; private set; }

    void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        if (_viewOnly)
        {
            return;
        }

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
        if (!_viewOnly && SendTouchOrPen(e, PointerStage.Pressed))
        {
            e.Handled = true;
        }
        else if (!_viewOnly && SendButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind))
        {
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_viewOnly && SendTouchOrPen(e, PointerStage.Released))
        {
            e.Handled = true;
        }
        else if (!_viewOnly && SendButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind))
        {
            e.Handled = true;
        }

        // The last button up ends the press, so anything the remote still thinks is down goes up with it, in case a
        // change went unreported.
        if (!_viewOnly && e.Pointer.Type == PointerType.Mouse && !AnyButton(e.GetCurrentPoint(this).Properties))
        {
            ReleaseButtons();
        }
    }

    /// <summary>
    /// Sends a mouse button going down or up, as <paramref name="kind"/> says, once: the remote keeps its own idea of
    /// what's held, so a change reported twice or a release of a button it never had down would confuse it.
    /// </summary>
    /// <returns>Whether <paramref name="kind"/> was a button changing.</returns>
    bool SendButton(PointerUpdateKind kind)
    {
        if (_viewOnly || ToButton(kind) is not { } button)
        {
            return false;
        }

        bool pressed = kind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed or PointerUpdateKind.MiddleButtonPressed
            or PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed;
        if (pressed ? _buttonsDown.Add(button) : _buttonsDown.Remove(button))
        {
            Session.SendPointerButton(button, pressed);
        }

        return true;
    }

    static bool AnyButton(PointerPointProperties p) =>
        p.IsLeftButtonPressed || p.IsRightButtonPressed || p.IsMiddleButtonPressed || p.IsXButton1Pressed || p.IsXButton2Pressed;

    /// <summary>Lets go of every button the remote thinks is held, as when the view loses the mouse.</summary>
    void ReleaseButtons()
    {
        foreach (var button in _buttonsDown.ToList())
        {
            _buttonsDown.Remove(button);
            Session.SendPointerButton(button, pressed: false);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SendTouchOrPen(e, PointerStage.Left);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (e.Pointer.Type == PointerType.Mouse)
        {
            ReleaseButtons();
        }

        int id = e.Pointer.Id;
        if (Session is ITouchInput touch)
        {
            if (_touches.Remove(id))
            {
                touch.SendTouch((ulong)id, TouchContactPhase.Canceled, 0, 0, null);
            }

            if (_pensInRange.Remove(id))
            {
                touch.SendPen((ulong)id, PenContactPhase.Canceled, 0, 0, default);
            }
        }
    }

    enum PointerStage { Pressed, Moved, Released, Left }

    /// <summary>
    /// Sends a finger or pen as itself when the session takes it (RDP's input channel), so the remote computer gets
    /// gestures and pen pressure. False when it should go as the mouse instead: the session doesn't take touch or pen
    /// (VNC, or an RDP server without the channel), or the contact began as the mouse.
    /// </summary>
    bool SendTouchOrPen(PointerEventArgs e, PointerStage stage)
    {
        if (Session is not ITouchInput touch || e.Pointer.Type is not (PointerType.Touch or PointerType.Pen))
        {
            return false;
        }

        int id = e.Pointer.Id;
        var (x, y) = RemotePoint(e) ?? (0, 0);
        if (e.Pointer.Type == PointerType.Touch)
        {
            switch (stage)
            {
                case PointerStage.Pressed when touch.AcceptsTouch:
                    _touches.Add(id);
                    touch.SendTouch((ulong)id, TouchContactPhase.Started, x, y, null);
                    return true;
                case PointerStage.Moved when _touches.Contains(id):
                    touch.SendTouch((ulong)id, TouchContactPhase.Moved, x, y, null);
                    return true;
                case PointerStage.Released when _touches.Remove(id):
                    touch.SendTouch((ulong)id, TouchContactPhase.Ended, x, y, null);
                    return true;
                default:
                    return false;
            }
        }

        var properties = e.GetCurrentPoint(this).Properties;
        bool touching = stage == PointerStage.Pressed || stage == PointerStage.Moved && properties.IsLeftButtonPressed;
        var state = new PenState(touching ? properties.Pressure : null, properties.IsBarrelButtonPressed, properties.IsEraser, properties.IsInverted,
            properties.XTilt, properties.YTilt, properties.Twist);
        if (stage == PointerStage.Left)
        {
            if (_pensInRange.Remove(id))
            {
                touch.SendPen((ulong)id, PenContactPhase.OutOfRange, x, y, state);
            }

            return false;
        }

        if (!_pensInRange.Contains(id))
        {
            // A pen that came in while the channel wasn't open stays the mouse until it leaves.
            if (stage == PointerStage.Released || !touch.AcceptsPen || stage == PointerStage.Moved && touching)
            {
                return false;
            }

            _pensInRange.Add(id);
        }

        touch.SendPen((ulong)id, touching ? PenContactPhase.Touching : PenContactPhase.Hovering, x, y, state);
        return true;
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

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateKeyCapture();
        _ = SendLocalClipboardAsync();
    }

    /// <summary>
    /// VNC, and RDP off Windows: hands the local clipboard to the server when the session gets the keyboard, if it changed since
    /// the last exchange. There's no portable "clipboard changed" event, and this is when a paste can follow.
    /// </summary>
    async Task SendLocalClipboardAsync()
    {
        // View only sends nothing to the remote computer, the clipboard included.
        if (_viewOnly || Session is not IClipboardSession { ExchangesClipboard: true } clipboardSession || Session.State != SessionState.Connected
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            // Files first: a file manager puts their names on the clipboard as text too.
            if (clipboardSession.ExchangesFiles && await SendLocalFilesAsync(clipboard, clipboardSession))
            {
                return;
            }

            if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            {
                if (text != _lastClipboard)
                {
                    _lastClipboard = text;
                    _lastImage = null;
                    _lastFiles = null;
                    clipboardSession.SendClipboard(text);
                }
            }
            else if (clipboardSession.ExchangesFiles)
            {
                await SendLocalImageAsync(clipboard, clipboardSession);
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            // Another app holds the clipboard open; the next focus tries again.
            Log.For("RemoteDesktop").LogWarning("Couldn't read the clipboard: {Message}", e.Message);
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        UpdateKeyCapture();
        ReleaseAllKeys();
    }

    // Switching windows keeps this control focused inside its window, so OnLostFocus doesn't fire.
    void OnWindowDeactivated(object? sender, EventArgs e)
    {
        UpdateKeyCapture();
        ReleaseAllKeys();
    }

    void OnWindowActivated(object? sender, EventArgs e)
    {
        UpdateKeyCapture();
        if (IsFocused)
        {
            _ = SendLocalClipboardAsync();
        }
    }

    /// <summary>
    /// The Windows key goes to the remote side while this session has the keyboard. Window activation isn't
    /// checked here: Avalonia raises Activated and GotFocus before IsActive turns true, so returning to the
    /// window would never re-capture. The hook checks that this window is in front at each key instead.
    /// </summary>
    void UpdateKeyCapture()
    {
        if (IsFocused && !_viewOnly && Session.State == SessionState.Connected && _topLevel?.TryGetPlatformHandle()?.Handle is { } window)
        {
            WindowsKeyHook.Capture(_windowsKey, window, _topLevel is IFullScreenHost { IsFullScreen: true }, SessionShortcuts);
        }
        else
        {
            WindowsKeyHook.Release(_windowsKey);
        }
    }

    /// <summary>A key the hook took from Windows. Alt or Ctrl held with Tab or Esc already went to the session as usual.</summary>
    internal void OnHookedKey(HookedKey key, bool pressed)
    {
        if (_viewOnly)
        {
            return;
        }

        if (Session is IKeysymInput keysyms)
        {
            // Super_L, Super_R, Tab, Escape
            keysyms.SendKeysym(key switch { HookedKey.LeftWindows => 0xFFEBu, HookedKey.RightWindows => 0xFFECu, HookedKey.Tab => 0xFF09u, _ => 0xFF1Bu }, pressed);
        }
        else
        {
            Session.SendKey(key switch { HookedKey.LeftWindows => (ushort)0xE05B, HookedKey.RightWindows => (ushort)0xE05C, HookedKey.Tab => (ushort)0x0F, _ => (ushort)0x01 }, pressed);
        }
    }

    // In or out of full screen: Alt+Tab and the rest may change sides.
    void OnFullScreenChanged(object? sender, EventArgs e) => UpdateKeyCapture();

    /// <summary>
    /// Where Alt+Tab, Alt+Esc and Ctrl+Esc go for this session: its connection's choice, else Settings'. Asked each time
    /// the session takes the keyboard; null is mstsc's default, full screen only.
    /// </summary>
    public Func<SystemShortcuts>? Shortcuts { get; init; }

    /// <summary>The saved choice for this session's connection, else Settings'.</summary>
    public SystemShortcuts SavedShortcuts => Shortcuts?.Invoke() ?? SystemShortcuts.FullScreen;

    // A choice made for this session only, from its This session tab; null follows the saved one.
    SystemShortcuts? _sessionShortcuts;

    /// <summary>
    /// Where Alt+Tab and the rest go for this session now: the saved choice, or one made for this session only, which
    /// lasts until the tab closes. Setting it takes effect at once.
    /// </summary>
    public SystemShortcuts SessionShortcuts
    {
        get => _sessionShortcuts ?? SavedShortcuts;
        set
        {
            _sessionShortcuts = value == SavedShortcuts ? null : value;
            UpdateKeyCapture();
        }
    }

    /// <summary>The choice behind <see cref="Shortcuts"/> may have changed: takes it up now if this session has the keyboard.</summary>
    public void RefreshShortcuts() => UpdateKeyCapture();

    void ReleaseAllKeys()
    {
        _keysymsDown.Clear();
        Session.ReleaseAllKeys();
        ReleaseButtons();
    }

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
        SizeImage();

        // Whole-number zoom (200%) stays crisp with plain pixel doubling; other factors need smoothing.
        bool wholeZoom = Math.Abs(displayScale - Math.Round(displayScale)) < 0.01;
        var interpolation = !_zoom || displayScale <= 1 ? BitmapInterpolationMode.Unspecified
            : wholeZoom ? BitmapInterpolationMode.None
            : BitmapInterpolationMode.HighQuality;
        RenderOptions.SetBitmapInterpolationMode(_image, interpolation);
        RenderOptions.SetBitmapInterpolationMode(_piecesLayer, interpolation);
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

        _onScreen = true;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            UpdateScale();
            _topLevel.ScalingChanged += OnScalingChanged;
            if (_topLevel is WindowBase window)
            {
                window.Deactivated += OnWindowDeactivated;
                window.Activated += OnWindowActivated;
            }

            if (_topLevel is IFullScreenHost host)
            {
                host.FullScreenChanged += OnFullScreenChanged;
            }
        }

        _cursors ??= new RemotePointerCursors();
        ShowRemotePointer();

        // TabControl detaches hidden tabs; repaint everything when this one comes back.
        if (Session.State == SessionState.Connected)
        {
            OnFrameUpdated(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Session.Width, Session.Height), resized: true));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _onScreen = false;
        _resizeTimer.Stop();
        _firstAskComing = false;
        WindowsKeyHook.Release(_windowsKey);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            if (_topLevel is WindowBase window)
            {
                window.Deactivated -= OnWindowDeactivated;
                window.Activated -= OnWindowActivated;
            }

            if (_topLevel is IFullScreenHost host)
            {
                host.FullScreenChanged -= OnFullScreenChanged;
            }

            _topLevel = null;
        }

        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _image.Cursor = null;
        _cursors?.Dispose();
        _cursors = null;
    }
}

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

/// <summary>
/// The cursor to show over a remote desktop for its pointer's shape. A standard pointer (the arrow, the text beam, the
/// hand, the resize arrows...) is shown as this computer's own, so the local pointer scheme, size and high contrast
/// carry over; a shape an app drew itself is shown as the server sent it. UI thread only.
/// </summary>
sealed class RemotePointerCursors : IDisposable
{
    const int MaxCustom = 32;

    static readonly Dictionary<StandardCursorType, Cursor> Standard = [];

    // The shapes seen lately, with what each was shown as; a server sends the same few again and again.
    readonly List<(RemotePointerShape Shape, PointerKind? Kind, Cursor Cursor, Bitmap? Bitmap)> _seen = [];

    /// <summary>The outlines of the standard pointers at a size, to recognize shapes by; empty where there are none.</summary>
    internal Func<int, int, IReadOnlyList<PointerOutline>> Outlines { get; init; } = WindowsPointerOutlines.For;

    /// <summary>What the last shape was recognized as; null for one shown as sent. For tests.</summary>
    internal PointerKind? LastKind { get; private set; }

    public Cursor For(RemotePointerShape? shape)
    {
        LastKind = null;
        if (shape is null)
        {
            LastKind = PointerKind.Arrow;
            return StandardCursor(StandardCursorType.Arrow);
        }

        if (shape.IsHidden)
        {
            return StandardCursor(StandardCursorType.None);
        }

        foreach (var seen in _seen)
        {
            if (seen.Shape.Width == shape.Width && seen.Shape.Height == shape.Height && seen.Shape.HotspotX == shape.HotspotX
                && seen.Shape.HotspotY == shape.HotspotY && seen.Shape.Rgba.AsSpan().SequenceEqual(shape.Rgba))
            {
                LastKind = seen.Kind;
                return seen.Cursor;
            }
        }

        var kind = PointerKinds.Recognize(shape, Outlines(shape.Width, shape.Height));
        Cursor cursor;
        Bitmap? bitmap = null;
        if (kind is { } known)
        {
            cursor = StandardCursor(StandardType(known));
        }
        else
        {
            bitmap = ToBitmap(shape);
            cursor = new Cursor(bitmap, new PixelPoint(shape.HotspotX, shape.HotspotY));
        }

        if (_seen.Count == MaxCustom)
        {
            var (_, _, oldCursor, oldBitmap) = _seen[0];
            _seen.RemoveAt(0);
            Release(oldCursor, oldBitmap);
        }

        _seen.Add((shape, kind, cursor, bitmap));
        LastKind = kind;
        return cursor;
    }

    public void Dispose()
    {
        foreach (var (_, _, cursor, bitmap) in _seen)
        {
            Release(cursor, bitmap);
        }

        _seen.Clear();
    }

    static void Release(Cursor cursor, Bitmap? bitmap)
    {
        // Standard cursors are shared; only a shape's own is this cache's to free.
        if (bitmap is not null)
        {
            cursor.Dispose();
            bitmap.Dispose();
        }
    }

    static Cursor StandardCursor(StandardCursorType type)
    {
        if (!Standard.TryGetValue(type, out var cursor))
        {
            cursor = new Cursor(type);
            Standard[type] = cursor;
        }

        return cursor;
    }

    internal static StandardCursorType StandardType(PointerKind kind) => kind switch
    {
        PointerKind.IBeam => StandardCursorType.Ibeam,
        PointerKind.Hand => StandardCursorType.Hand,
        PointerKind.Wait => StandardCursorType.Wait,
        PointerKind.AppStarting => StandardCursorType.AppStarting,
        PointerKind.Cross => StandardCursorType.Cross,
        PointerKind.Help => StandardCursorType.Help,
        PointerKind.No => StandardCursorType.No,
        PointerKind.SizeAll => StandardCursorType.SizeAll,
        PointerKind.SizeNorthSouth => StandardCursorType.SizeNorthSouth,
        PointerKind.SizeWestEast => StandardCursorType.SizeWestEast,
        PointerKind.SizeNorthwestSoutheast => StandardCursorType.TopLeftCorner,
        PointerKind.SizeNortheastSouthwest => StandardCursorType.TopRightCorner,
        PointerKind.UpArrow => StandardCursorType.UpArrow,
        _ => StandardCursorType.Arrow,
    };

    static unsafe Bitmap ToBitmap(RemotePointerShape shape)
    {
        var bitmap = new WriteableBitmap(new PixelSize(shape.Width, shape.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var frame = bitmap.Lock();
        int rowBytes = shape.Width * 4;
        for (int row = 0; row < shape.Height; row++)
        {
            Marshal.Copy(shape.Rgba, row * rowBytes, frame.Address + row * frame.RowBytes, rowBytes);
        }

        return bitmap;
    }
}

/// <summary>
/// The outlines of Windows' standard pointers, from the default scheme's files (what a Windows server draws unless
/// its user picked another scheme) and the text beam and cross built into Windows. Nothing elsewhere: a remote
/// desktop's shapes are then shown as sent.
/// </summary>
static class WindowsPointerOutlines
{
    static readonly Dictionary<(int, int), IReadOnlyList<PointerOutline>> BySize = [];

    // The default scheme's files in %WINDIR%\Cursors, and the inverted scheme's text beam and cross.
    static readonly (PointerKind Kind, string File)[] Files =
    [
        (PointerKind.Arrow, "aero_arrow.cur"),
        (PointerKind.Hand, "aero_link.cur"),
        (PointerKind.Wait, "aero_busy.ani"),
        (PointerKind.AppStarting, "aero_working.ani"),
        (PointerKind.Help, "aero_helpsel.cur"),
        (PointerKind.No, "aero_unavail.cur"),
        (PointerKind.SizeAll, "aero_move.cur"),
        (PointerKind.SizeNorthSouth, "aero_ns.cur"),
        (PointerKind.SizeWestEast, "aero_ew.cur"),
        (PointerKind.SizeNorthwestSoutheast, "aero_nwse.cur"),
        (PointerKind.SizeNortheastSouthwest, "aero_nesw.cur"),
        (PointerKind.UpArrow, "aero_up.cur"),
        (PointerKind.IBeam, "beam_i.cur"),
        (PointerKind.Cross, "cross_i.cur"),
    ];

    // OCR_IBEAM and OCR_CROSS: the default scheme's text beam and cross have no file.
    static readonly (PointerKind Kind, int Id)[] BuiltIn = [(PointerKind.IBeam, 32513), (PointerKind.Cross, 32515)];

    public static IReadOnlyList<PointerOutline> For(int width, int height)
    {
        if (!OperatingSystem.IsWindows() || width is < 1 or > RemotePointerShape.MaxSide || height is < 1 or > RemotePointerShape.MaxSide)
        {
            return [];
        }

        if (BySize.TryGetValue((width, height), out var outlines))
        {
            return outlines;
        }

        var found = new List<PointerOutline>();
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors");
            foreach (var (kind, file) in Files)
            {
                var path = Path.Combine(folder, file);
                if (File.Exists(path) && Render(kind, LoadImage(IntPtr.Zero, path, ImageCursor, width, height, LoadFromFile), width, height, owned: true) is { } outline)
                {
                    found.Add(outline);
                }
            }

            foreach (var (kind, id) in BuiltIn)
            {
                if (Render(kind, LoadImage(IntPtr.Zero, id, ImageCursor, width, height, Shared), width, height, owned: false) is { } outline)
                {
                    found.Add(outline);
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            Log.For("RemoteDesktop").LogWarning("Couldn't read Windows' standard pointers: {Message}", e.Message);
        }

        BySize[(width, height)] = found;
        return found;
    }

    /// <summary>
    /// A cursor file as an RDP server sends it: straight-alpha RGBA, with pixels that invert what's under them drawn as a
    /// checked pattern, as IronRDP decodes them. For tests.
    /// </summary>
    internal static RemotePointerShape? AsServerSends(string file, int size)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", file);
        var cursor = LoadImage(IntPtr.Zero, path, ImageCursor, size, size, LoadFromFile);
        if (cursor == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!GetIconInfo(cursor, out var info) || Draw(cursor, size, size, 0) is not { } onBlack || Draw(cursor, size, size, 0x00FFFFFF) is not { } onWhite)
            {
                return null;
            }

            DeleteObject(info.Mask);
            DeleteObject(info.Color);
            var rgba = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                int alpha = 255 - ((int)(onWhite[i] & 0xFF) - (int)(onBlack[i] & 0xFF));
                if (alpha > 255)
                {
                    // Inverted: white on black, black on white.
                    byte shade = (i / size + i % size) % 2 == 0 ? (byte)255 : (byte)0;
                    (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (shade, shade, shade, 255);
                    continue;
                }

                alpha = Math.Clamp(alpha, 0, 255);
                for (int c = 0; c < 3; c++)
                {
                    int value = (int)((onBlack[i] >> (16 - c * 8)) & 0xFF);
                    rgba[i * 4 + c] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / alpha);
                }

                rgba[i * 4 + 3] = (byte)alpha;
            }

            return RemotePointerShape.Create(size, size, info.HotspotX, info.HotspotY, rgba);
        }
        finally
        {
            DestroyCursor(cursor);
        }
    }

    /// <summary>The outline of <paramref name="cursor"/> drawn at the size: what changes it makes to black and to white.</summary>
    static PointerOutline? Render(PointerKind kind, IntPtr cursor, int width, int height, bool owned)
    {
        if (cursor == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!GetIconInfo(cursor, out var info))
            {
                return null;
            }

            DeleteObject(info.Mask);
            DeleteObject(info.Color);
            var onBlack = Draw(cursor, width, height, 0x00000000);
            var onWhite = Draw(cursor, width, height, 0x00FFFFFF);
            if (onBlack is null || onWhite is null)
            {
                return null;
            }

            var covered = new bool[width * height];
            int min = PointerKinds.MinAlpha;
            for (int i = 0; i < covered.Length; i++)
            {
                uint b = onBlack[i], w = onWhite[i];
                bool lighter = (b & 0xFF) >= min || ((b >> 8) & 0xFF) >= min || ((b >> 16) & 0xFF) >= min;
                bool darker = (w & 0xFF) <= 255 - min || ((w >> 8) & 0xFF) <= 255 - min || ((w >> 16) & 0xFF) <= 255 - min;
                covered[i] = lighter || darker;
            }

            return new PointerOutline(kind, width, height, info.HotspotX, info.HotspotY, covered);
        }
        finally
        {
            if (owned)
            {
                DestroyCursor(cursor);
            }
        }
    }

    static uint[]? Draw(IntPtr cursor, int width, int height, uint background)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32,
            };
            var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var old = SelectObject(dc, bitmap);
                var pixels = new uint[width * height];
                Array.Fill(pixels, background);
                Marshal.Copy((int[])(object)pixels, 0, bits, pixels.Length);
                bool drawn = DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, DiNormal);
                GdiFlush();
                Marshal.Copy(bits, (int[])(object)pixels, 0, pixels.Length);
                SelectObject(dc, old);
                return drawn ? pixels : null;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    const uint ImageCursor = 2, LoadFromFile = 0x10, Shared = 0x8000, DiNormal = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    static extern IntPtr LoadImage(IntPtr instance, nint id, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll")]
    static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    static extern bool GdiFlush();
}

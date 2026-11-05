namespace Orrery.Core.Sessions;

/// <summary>
/// The remote desktop's pointer as its server sent it: RGBA pixels with straight (not premultiplied) alpha, row after
/// row, and the hotspot, the pixel that sits on the pointer's position. <see cref="Hidden"/> is a pointer the server
/// hid. Immutable, so a new shape is a new object.
/// </summary>
public sealed class RemotePointerShape
{
    /// <summary>The largest side kept: RDP's large pointers go up to 384 pixels.</summary>
    public const int MaxSide = 384;

    /// <summary>A pointer the server hid, as while typing or during a video.</summary>
    public static readonly RemotePointerShape Hidden = new(0, 0, 0, 0, []);

    RemotePointerShape(int width, int height, int hotspotX, int hotspotY, byte[] rgba)
    {
        Width = width;
        Height = height;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public int HotspotX { get; }
    public int HotspotY { get; }

    /// <summary>The pixels, 4 bytes each: red, green, blue, alpha. Empty when hidden.</summary>
    public byte[] Rgba { get; }

    public bool IsHidden => Width == 0;

    /// <summary>
    /// A shape from straight-alpha RGBA, or null if the sizes are out of range or don't match the pixels. A hotspot
    /// outside the shape is moved to its nearest edge.
    /// </summary>
    public static RemotePointerShape? Create(int width, int height, int hotspotX, int hotspotY, byte[] rgba)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide || rgba.Length != width * height * 4)
        {
            return null;
        }

        return new RemotePointerShape(width, height, Math.Clamp(hotspotX, 0, width - 1), Math.Clamp(hotspotY, 0, height - 1), rgba);
    }

    /// <summary>Straight alpha from premultiplied RGBA, in place.</summary>
    public static void Unpremultiply(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int alpha = rgba[i + 3];
            if (alpha is 0 or 255)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                rgba[i + c] = (byte)Math.Min(255, (rgba[i + c] * 255 + alpha / 2) / alpha);
            }
        }
    }

    /// <summary>
    /// Draws the pointer with its hotspot at (<paramref name="x"/>, <paramref name="y"/>) into RGB pixels or, with 4
    /// bytes a pixel, RGBA; <paramref name="shape"/> null draws <see cref="Recording.PointerArrow"/>, the usual arrow.
    /// </summary>
    public static void Draw(RemotePointerShape? shape, Span<byte> pixels, int width, int height, int x, int y, int bytesPerPixel = 3)
    {
        if (shape is null)
        {
            Recording.PointerArrow.Draw(pixels, width, height, x, y, bytesPerPixel);
            return;
        }

        int left = x - shape.HotspotX, top = y - shape.HotspotY;
        for (int row = 0; row < shape.Height; row++)
        {
            int py = top + row;
            if (py < 0 || py >= height)
            {
                continue;
            }

            for (int column = 0; column < shape.Width; column++)
            {
                int px = left + column;
                if (px < 0 || px >= width)
                {
                    continue;
                }

                var source = shape.Rgba.AsSpan((row * shape.Width + column) * 4, 4);
                int alpha = source[3];
                if (alpha == 0)
                {
                    continue;
                }

                var target = pixels.Slice((py * width + px) * bytesPerPixel, 3);
                for (int c = 0; c < 3; c++)
                {
                    target[c] = (byte)((source[c] * alpha + target[c] * (255 - alpha) + 127) / 255);
                }
            }
        }
    }
}

/// <summary>A remote desktop that tells Orrery what its pointer looks like (RDP, VNC).</summary>
public interface IRemotePointerSession
{
    /// <summary>The pointer's shape now; null for the usual arrow, as before the server has sent one.</summary>
    RemotePointerShape? PointerShape { get; }

    /// <summary>Raised on a background thread when <see cref="PointerShape"/> changes.</summary>
    event EventHandler? PointerShapeChanged;
}

using System.Runtime.InteropServices;

namespace Orrery.Core.Sessions;

/// <summary>Copies part of a session's frame into a buffer of just that part, for the view and the recorder.</summary>
public static class FrameCopy
{
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Copies <paramref name="region"/>, clipped to <paramref name="width"/> × <paramref name="height"/>, as RGBA rows
    /// one after another into <paramref name="scratch"/> (grown as needed), and returns the part copied.
    /// </summary>
    public static FrameRect CopyOut(IFrameBufferSession session, FrameRect region, int width, int height, ref byte[]? scratch)
    {
        int x = Math.Clamp(region.X, 0, width), y = Math.Clamp(region.Y, 0, height);
        var clipped = new FrameRect(x, y, Math.Clamp(region.Right, x, width) - x, Math.Clamp(region.Bottom, y, height) - y);
        if (clipped.IsEmpty)
        {
            return clipped;
        }

        int rowBytes = clipped.Width * BytesPerPixel, size = rowBytes * clipped.Height;
        if (scratch is null || scratch.Length < size)
        {
            scratch = new byte[size];
        }

        var pinned = GCHandle.Alloc(scratch, GCHandleType.Pinned);
        try
        {
            // CopyRegion writes each pixel at its place in a whole frame of the given stride, so the frame's origin
            // goes where the region's first pixel lands at the start of the buffer.
            var origin = pinned.AddrOfPinnedObject() - ((nint)y * rowBytes + (nint)x * BytesPerPixel);
            session.CopyRegion(clipped, origin, rowBytes);
        }
        finally
        {
            pinned.Free();
        }

        return clipped;
    }
}

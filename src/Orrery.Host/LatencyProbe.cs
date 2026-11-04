using System.Diagnostics;
using System.Runtime.Versioning;
using Orrery.Host.Platform.Windows;

namespace Orrery.Host;

/// <summary>
/// <c>--measure</c>: times frames from this desktop to the same frame showing in OrreryStreamViewer on this desktop,
/// with no camera. Each frame sent gets a <see cref="FrameMarker"/>; each desktop captured afterwards is checked for
/// the marker inside the viewer's window. The time between a frame being presented here and Desktop Duplication
/// seeing it in the viewer covers capture, encode, network, decode, present and the compositor: everything but the
/// display's own scan-out and pixel response. The viewer showing a new frame changes the desktop, so the loop keeps
/// itself going.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class LatencyProbe : IDisposable
{
    const int StagingWidth = 2048;
    const int StagingHeight = 128;

    readonly nint _context;
    readonly DisplayOutput _monitor;
    readonly int _frameWidth;
    readonly int _frameHeight;
    readonly byte[] _marker = new byte[FrameMarker.Width * FrameMarker.Height * 4];
    readonly long[] _stamped = new long[FrameMarker.Modulus];
    nint _staging;
    long _newest;
    long _lastSeen;
    bool _pending;
    long _pendingPresent;
    double _scaleX;
    double _scaleY;

    public LatencyProbe(nint device, nint context, DisplayOutput monitor, int frameWidth, int frameHeight)
    {
        _context = context;
        _monitor = monitor;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        _staging = Dxgi.CreateStagingTexture(device, StagingWidth, StagingHeight);
    }

    /// <summary>Milliseconds from each frame's present here to its first sight in the viewer.</summary>
    public List<double> Latencies { get; } = [];

    /// <summary>Desktop frames in which no viewer surface was found on the captured monitor.</summary>
    public int NotVisible { get; private set; }

    /// <summary>Desktop frames where the viewer was found but showed no readable marker (before its first frame, say).</summary>
    public int Unreadable { get; private set; }

    /// <summary>
    /// Queues a copy of where the viewer shows its marker in <paramref name="desktop"/>, the captured desktop presented
    /// at <paramref name="presented"/>. Call before the frame is released; <see cref="Read"/> reads the copy.
    /// </summary>
    public void Look(nint desktop, long presented)
    {
        if (!ViewerWindow.TryFindClientArea(out int left, out int top, out int width, out int height))
        {
            NotVisible++;
            return;
        }

        int x = left - _monitor.Left, y = top - _monitor.Top;
        _scaleX = width / (double)_frameWidth;
        _scaleY = height / (double)_frameHeight;
        int regionWidth = Math.Min((int)Math.Ceiling(FrameMarker.Width * _scaleX) + 1, StagingWidth);
        int regionHeight = Math.Min((int)Math.Ceiling(FrameMarker.Height * _scaleY) + 1, StagingHeight);
        if (x < 0 || y < 0 || x + regionWidth > _monitor.Width || y + regionHeight > _monitor.Height)
        {
            NotVisible++;
            return;
        }

        Dxgi.CopyRegion(_context, _staging, desktop, new Dxgi.Box(x, y, x + regionWidth, y + regionHeight));
        _pending = true;
        _pendingPresent = presented;
    }

    /// <summary>Draws frame <paramref name="frame"/>'s marker into the surface about to be encoded, and notes when it was presented here.</summary>
    public void Stamp(nint surfaceTexture, long frame, long presented)
    {
        FrameMarker.Draw(_marker, frame);
        Dxgi.Upload(_context, surfaceTexture, new Dxgi.Box(0, 0, FrameMarker.Width, FrameMarker.Height), _marker, FrameMarker.Width * 4);
        _stamped[frame % FrameMarker.Modulus] = presented;
        _newest = frame;
    }

    /// <summary>Reads the marker <see cref="Look"/> copied, once the GPU has copied it; call after the frame is encoded.</summary>
    public void Read()
    {
        if (!_pending)
        {
            return;
        }

        _pending = false;
        ushort bits = Dxgi.ReadStaging(_context, _staging, StagingHeight, (pixels, pitch) => FrameMarker.Read(pixels, pitch, _scaleX, _scaleY));
        if (!FrameMarker.TryDecode(bits, out int number))
        {
            Unreadable++;
            return;
        }

        long frame = FrameMarker.Resolve(number, _newest);
        if (frame <= _lastSeen || frame <= 0)
        {
            return; // still showing a frame already counted
        }

        _lastSeen = frame;
        double latency = (_pendingPresent - _stamped[frame % FrameMarker.Modulus]) * 1000.0 / Stopwatch.Frequency;
        if (latency >= 0)
        {
            Latencies.Add(latency);
        }
    }

    public void Dispose()
    {
        Dxgi.ReleaseCom(_staging);
        _staging = 0;
    }
}

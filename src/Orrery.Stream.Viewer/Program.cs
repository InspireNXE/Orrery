using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Orrery.Protocols.Stream.Windows;
using Orrery.Streaming;

namespace Orrery.Stream.Viewer;

/// <summary>
/// <c>OrreryStreamViewer [--port N] [--codec av1|hevc|h264]</c>: listens for <c>OrreryHost --capture --send</c>,
/// decodes each frame on the GPU and presents it, until the window is closed. Start it before the host, since the
/// stream only starts on the first frame's keyframe.
/// </summary>
static class Program
{
    const int DefaultPort = 47990;

    static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("The viewer runs on Windows only so far.");
            return 1;
        }

        int port = DefaultPort;
        int? seconds = null;
        bool avalonia = false;
        var codec = VideoCodec.Av1;
        for (int i = 0; i < args.Length; i++)
        {
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--port" when int.TryParse(value, CultureInfo.InvariantCulture, out int p) && p is > 0 and < 65536:
                    port = p;
                    break;
                case "--codec" when value?.ToLowerInvariant() is "av1" or "hevc" or "h265" or "h264" or "avc":
                    codec = value.ToLowerInvariant() switch { "av1" => VideoCodec.Av1, "h264" or "avc" => VideoCodec.H264, _ => VideoCodec.Hevc };
                    break;
                case "--seconds" when int.TryParse(value, CultureInfo.InvariantCulture, out int s) && s > 0:
                    seconds = s;
                    break;
                case "--avalonia":
                    avalonia = true;
                    continue; // takes no value
                default:
                    Console.WriteLine($"Unknown or invalid option {args[i]}.");
                    Console.WriteLine($"OrreryStreamViewer [--port N (default {DefaultPort})] [--codec av1|hevc|h264 (default av1)] [--seconds N (close after)]"
                        + " [--avalonia (present inside an Avalonia window)]");
                    return 1;
            }

            i++;
        }

        var session = new StreamSession(port, codec);
        Console.WriteLine($"Listening on UDP {port} for {codec}{(avalonia ? ", inside an Avalonia window" : "")}. Close the window to stop.");
        if (avalonia)
        {
            AvaloniaViewer.Run(session, seconds);
        }
        else
        {
            RunBare(session, $"Orrery Stream Viewer: waiting for {codec} on UDP {port}", seconds);
        }

        return session.Stop(Console.Out);
    }

    /// <summary>Presents into a top-level Win32 window of its own, the shortest path to the screen.</summary>
    [SupportedOSPlatform("windows")]
    static void RunBare(StreamSession session, string title, int? seconds)
    {
        var window = StreamWindow.Open(title, 1280, 720);
        using var closer = seconds is { } s ? new Timer(_ => window.Close(), null, TimeSpan.FromSeconds(s), Timeout.InfiniteTimeSpan) : null;
        session.Start(window.Handle, window.SetTitle);
        StreamWindow.RunMessageLoop();
    }
}

/// <summary>
/// One stream into one window: a thread that receives, decodes and presents, so each frame goes from the socket to
/// the screen with no queue.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class StreamSession(int port, VideoCodec codec)
{
    readonly UdpMediaReceiver _receiver = new(new IPEndPoint(IPAddress.Any, port));
    readonly ViewerStats _stats = new();
    Thread? _worker;
    Exception? _failure;
    bool _stopped;

    /// <summary>Starts presenting into <paramref name="hwnd"/>, reporting progress through <paramref name="setTitle"/> (called from the worker).</summary>
    public void Start(nint hwnd, Action<string> setTitle)
    {
        _worker = new Thread(() =>
        {
            try
            {
                Receive(hwnd, setTitle);
            }
            catch (Exception e) when (e is COMException or NotSupportedException)
            {
                _failure = e;
                setTitle($"Orrery Stream Viewer: {e.Message}");
                _receiver.Dispose();
            }
        }) { Name = "Receive, decode, present" };
        _worker.Start();
    }

    /// <summary>Stops receiving and waits for the worker, before its window goes away. Safe to call twice.</summary>
    public void StopReceiving()
    {
        _receiver.Dispose();
        _worker?.Join();
    }

    /// <summary>Stops, reports what was measured, and returns the exit code.</summary>
    public int Stop(TextWriter output)
    {
        StopReceiving();
        if (_stopped)
        {
            return _failure is null ? 0 : 1;
        }

        _stopped = true;
        if (_failure is not null)
        {
            output.WriteLine($"Stopped: {_failure.Message}");
        }

        _stats.Report(output, _receiver.Assembler);
        return _failure is null ? 0 : 1;
    }

    void Receive(nint hwnd, Action<string> setTitle)
    {
        _ = CoInitializeEx(0, 0); // COINIT_MULTITHREADED; Media Foundation wants COM on its calling thread.
        nint device = D3D11Presenter.CreateDevice();
        try
        {
            using var decoder = MediaFoundationDecoder.Create(device, codec);
            using var presenter = new D3D11Presenter(device, hwnd);
            setTitle($"Orrery Stream Viewer: {decoder.Name}, waiting on UDP {port}");
            long lastTitle = Stopwatch.GetTimestamp();
            _receiver.Run(frame =>
            {
                long received = Stopwatch.GetTimestamp();
                if (!decoder.Decode(frame.Data, (long)frame.CaptureMicroseconds * 10, out var decoded))
                {
                    _stats.Waiting++;
                    return;
                }

                long decodedAt = Stopwatch.GetTimestamp();
                presenter.Present(decoded!);
                long presented = Stopwatch.GetTimestamp();
                _stats.Add(Milliseconds(decodedAt - received), Milliseconds(presented - decodedAt),
                    ClockMilliseconds(presented) - frame.CaptureMicroseconds / 1000.0, decoded!.Width, decoded.Height);
                if (Milliseconds(presented - lastTitle) >= 1000)
                {
                    lastTitle = presented;
                    setTitle($"Orrery Stream Viewer: {decoded.Width}x{decoded.Height} {codec}, {decoder.Name}, {_stats.Summary()}"
                        + (presenter.Tearing ? ", tearing" : ""));
                }
            });
        }
        finally
        {
            Com.Release(device);
        }
    }

    static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>A Stopwatch timestamp in milliseconds, on the same clock the host stamps frames with (QPC).</summary>
    static double ClockMilliseconds(long ticks) => ticks / (Stopwatch.Frequency / 1000.0);

    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern int CoInitializeEx(nint reserved, int flags);
}

/// <summary>What the viewer measured, kept by the receiving thread and reported once it has stopped.</summary>
sealed class ViewerStats
{
    readonly List<double> _decode = [];
    readonly List<double> _present = [];
    readonly List<double> _endToEnd = [];
    int _width;
    int _height;

    /// <summary>Frames the decoder took without a picture coming out: before the first keyframe, or after a lost frame.</summary>
    public int Waiting { get; set; }

    public void Add(double decode, double present, double endToEnd, int width, int height)
    {
        _decode.Add(decode);
        _present.Add(present);
        _endToEnd.Add(endToEnd);
        (_width, _height) = (width, height);
    }

    public string Summary() =>
        $"{_decode.Count} frames, decode {Median(_decode):0.00} ms, host to here {Median(_endToEnd):0.00} ms";

    public void Report(TextWriter output, FrameAssembler assembler)
    {
        output.WriteLine($"{_decode.Count} frames shown at {_width}x{_height}; {Waiting} decoded without a picture"
            + $"; {assembler.FramesDropped} dropped incomplete, {assembler.PacketsRejected} datagrams rejected.");
        if (_decode.Count < 2)
        {
            return;
        }

        // The first frame includes the decoder and swapchain starting up.
        output.WriteLine("After the first frame, median / 95th percentile / worst:");
        output.WriteLine($"  received to decoded    {Spread(_decode.Skip(1))}");
        output.WriteLine($"  decoded to presented   {Spread(_present.Skip(1))}");
        output.WriteLine($"  host present to here   {Spread(_endToEnd.Skip(1))}  (only meaningful when the host is this machine)");
    }

    static string Spread(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return $"{Percentile(sorted, 0.5):0.00} / {Percentile(sorted, 0.95):0.00} / {sorted[^1]:0.00} ms";
    }

    static double Median(List<double> values) => values.Count == 0 ? 0 : Percentile(values.Order().ToList(), 0.5);

    static double Percentile(List<double> sorted, double fraction) =>
        sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];
}

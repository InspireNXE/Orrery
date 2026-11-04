using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Orrery.Host.Encoding;
using Orrery.Host.Encoding.Amf;
using Orrery.Host.Platform.Windows;
using Orrery.Streaming;

namespace Orrery.Host;

/// <summary>What <c>--capture</c> was asked to do.</summary>
/// <param name="Send">Where to send the stream over plain UDP (an IP address and port), for OrreryStreamViewer.</param>
sealed record CaptureOptions(int Monitor = 0, VideoCodec Codec = VideoCodec.Av1, int Seconds = 10, long? Bitrate = null, string? OutputPath = null,
    bool Loopback = false, IPEndPoint? Send = null)
{
    /// <summary>Reads the arguments after <c>--capture</c>, or returns null with <paramref name="error"/> saying what's wrong.</summary>
    public static CaptureOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        var options = new CaptureOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string? value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--monitor" when int.TryParse(value, CultureInfo.InvariantCulture, out int monitor) && monitor >= 0:
                    options = options with { Monitor = monitor };
                    break;
                case "--codec" when ParseCodec(value) is { } codec:
                    options = options with { Codec = codec };
                    break;
                case "--seconds" when int.TryParse(value, CultureInfo.InvariantCulture, out int seconds) && seconds is > 0 and <= 600:
                    options = options with { Seconds = seconds };
                    break;
                case "--mbps" when int.TryParse(value, CultureInfo.InvariantCulture, out int mbps) && mbps is > 0 and <= 1000:
                    options = options with { Bitrate = mbps * 1_000_000L };
                    break;
                case "--out" when !string.IsNullOrWhiteSpace(value):
                    options = options with { OutputPath = value };
                    break;
                case "--send" when IPEndPoint.TryParse(value ?? "", out var destination) && destination.Port != 0:
                    options = options with { Send = destination };
                    break;
                case "--loopback":
                    options = options with { Loopback = true };
                    continue; // takes no value
                default:
                    error = value is null || args[i] is not ("--monitor" or "--codec" or "--seconds" or "--mbps" or "--out" or "--send")
                        ? $"Unknown option {args[i]}."
                        : $"{args[i]} {value} isn't valid.";
                    return null;
            }

            i++;
        }

        if (options.Loopback && options.Send is not null)
        {
            error = "--loopback and --send can't be used together.";
            return null;
        }

        error = null;
        return options;
    }

    static VideoCodec? ParseCodec(string? value) => value?.ToLowerInvariant() switch
    {
        "av1" => VideoCodec.Av1,
        "hevc" or "h265" => VideoCodec.Hevc,
        "h264" or "avc" => VideoCodec.H264,
        _ => null,
    };

    /// <summary>
    /// The bitrate to use when none was asked for: an eighth of a bit per pixel per second (about 15 Mbps at 1080p60,
    /// 60 at 4K60, 125 at 4K120), within 10 to 150 Mbps.
    /// </summary>
    public static long DefaultBitrate(int width, int height, double framesPerSecond) =>
        Math.Clamp((long)(width * (long)height * framesPerSecond / 8), 10_000_000, 150_000_000);
}

/// <summary>
/// <c>OrreryHost --capture</c>: duplicates one monitor and encodes every frame that changes, the host's half of phase 1
/// with nothing sent anywhere. Reports how long each frame takes from being presented to being a bitstream, and can
/// write the bitstream to a file to play back. AMD (AMF) only so far.
/// </summary>
static class CaptureTest
{
    public static int Run(TextWriter output, IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Capture runs on Windows only so far.");
            return 1;
        }

        var options = CaptureOptions.Parse(args, out var error);
        if (options is null)
        {
            output.WriteLine(error);
            return 1;
        }

        var outputs = Dxgi.Outputs();
        for (int i = 0; i < outputs.Count; i++)
        {
            var o = outputs[i];
            output.WriteLine($"Monitor {i}: {o.DeviceName}, {o.Width}x{o.Height} at {o.Left},{o.Top}, on {o.AdapterName}{(i == options.Monitor ? "  <- capturing" : "")}");
        }

        if (options.Monitor >= outputs.Count)
        {
            output.WriteLine($"There's no monitor {options.Monitor}.");
            return 1;
        }

        var monitor = outputs[options.Monitor];
        if (monitor.VendorId != GpuAdapter.Amd)
        {
            output.WriteLine("That monitor isn't on an AMD GPU; only AMF encodes from capture so far.");
            return 1;
        }

        var runtime = AmfRuntime.TryLoad(out var reason);
        if (runtime is null)
        {
            output.WriteLine($"AMF is unavailable: {reason}.");
            return 1;
        }

        try
        {
            return Capture(output, runtime, monitor, options);
        }
        catch (Exception e) when (e is AmfException or COMException)
        {
            output.WriteLine($"Capture failed: {e.Message}");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    static int Capture(TextWriter output, AmfRuntime runtime, DisplayOutput monitor, CaptureOptions options)
    {
        nint device = Dxgi.CreateDevice(monitor.AdapterIndex);
        nint immediate = 0;
        DesktopDuplication? duplication = null;
        try
        {
            Dxgi.ProtectMultithreaded(device);
            immediate = Dxgi.ImmediateContext(device);
            using var context = runtime.CreateContext(device);
            duplication = DesktopDuplication.Open(device, monitor);
            int width = duplication.Width, height = duplication.Height;
            if (duplication.Rotated)
            {
                output.WriteLine("The monitor is rotated; frames are captured unrotated.");
            }

            if (!SurfacesMatch(output, context, duplication))
            {
                return 1;
            }

            var codec = AmfCodec.All.Single(c => c.Codec == options.Codec);
            uint fps = (uint)Math.Round(duplication.RefreshRate);
            long bitrate = options.Bitrate ?? CaptureOptions.DefaultBitrate(width, height, fps);
            using var encoder = context.CreateComponent(codec.ComponentId);
            AmfTestEncoder.Configure(encoder, codec, width, height, fps, bitrate);
            // BGRA straight from the desktop: the encoder converts to YUV itself, on the GPU.
            encoder.Init(Amf.SurfaceBgra, width, height);

            string? path = options.OutputPath is { } requested
                ? Path.ChangeExtension(Path.GetFullPath(requested), BitstreamFile.Extension(options.Codec))
                : null;
            using var file = path is null ? null : new BitstreamFile(File.Create(path), options.Codec, width, height);
            // With --loopback the file is written from the frames that came back over UDP, which proves they arrive whole.
            ulong? firstReceived = null;
            using var loopback = options.Loopback
                ? new Loopback(f =>
                {
                    firstReceived ??= f.CaptureMicroseconds;
                    file?.Write(f.Data, (long)((f.CaptureMicroseconds - firstReceived.Value) / 1000));
                })
                : null;
            using var sender = options.Send is { } destination ? new UdpMediaSender(destination) : null;
            output.WriteLine($"Encoding {width}x{height} at up to {fps} fps with {Name(options.Codec)}, {bitrate / 1_000_000} Mbps, for {options.Seconds} s"
                + (loopback is not null ? ", sending each frame over UDP to 127.0.0.1 and back together."
                    : sender is not null ? $", sending to {options.Send} over UDP (start OrreryStreamViewer there first)."
                    : ".")
                + " A frame is only captured when something on the monitor changes, so move a window or play a video.");

            var stats = new List<FrameTiming>();
            long totalBytes = 0, firstPresent = 0;
            bool startsCorrectly = false;
            int pointerOnly = 0, reopened = 0;
            long deadline = Stopwatch.GetTimestamp() + options.Seconds * Stopwatch.Frequency;
            while (duplication is not null && Stopwatch.GetTimestamp() < deadline)
            {
                var result = duplication.Acquire(100, out var texture, out long presented);
                switch (result)
                {
                    case DuplicationResult.Timeout:
                        continue;
                    case DuplicationResult.PointerOnly:
                        pointerOnly++;
                        duplication.ReleaseFrame();
                        continue;
                    case DuplicationResult.AccessLost:
                        duplication.Dispose();
                        duplication = null;
                        reopened++;
                        duplication = Reopen(device, monitor, deadline);
                        if (duplication is null || duplication.Width != width || duplication.Height != height)
                        {
                            output.WriteLine("The monitor's mode changed or it stopped being available; stopping.");
                            duplication?.Dispose();
                            duplication = null;
                        }

                        continue;
                }

                long captured = Stopwatch.GetTimestamp();
                nint surface = 0;
                try
                {
                    surface = context.AllocSurface(Amf.SurfaceBgra, width, height);
                    Dxgi.CopyResource(immediate, AmfContext.SurfaceTexture(surface), texture);
                }
                finally
                {
                    Dxgi.ReleaseCom(texture);
                    duplication.ReleaseFrame();
                }

                byte[] frame;
                try
                {
                    AmfCom.Check("AMFComponent::SubmitInput", encoder.SubmitInput(surface));
                    nint data;
                    AMF_RESULT query;
                    while ((query = encoder.QueryOutput(out data)) == AMF_RESULT.AMF_REPEAT)
                    {
                    }

                    AmfCom.Check("AMFComponent::QueryOutput", query);
                    try
                    {
                        frame = AmfComponent.ReadBuffer(data);
                    }
                    finally
                    {
                        AmfCom.Release(data);
                    }
                }
                finally
                {
                    AmfCom.Release(surface);
                }

                long encoded = Stopwatch.GetTimestamp();
                if (stats.Count == 0)
                {
                    firstPresent = presented;
                    startsCorrectly = AmfTestEncoder.StartsCorrectly(options.Codec, frame);
                }

                stats.Add(new FrameTiming(Milliseconds(captured - presented), Milliseconds(encoded - captured), frame.Length));
                totalBytes += frame.Length;
                // Frames carry their present time on this machine's QPC clock, so a viewer here can time the whole trip.
                ulong presentMicroseconds = (ulong)(presented / (Stopwatch.Frequency / 1_000_000.0));
                sender?.SendFrame(frame, new MediaPacketHeader(MediaKind.Video, stats.Count == 1 ? MediaPacketFlags.Recovery : MediaPacketFlags.None,
                    0, (uint)stats.Count, 0, 0, presentMicroseconds));
                if (loopback is not null)
                {
                    loopback.Send(frame, presentMicroseconds, recovery: stats.Count == 1);
                }
                else
                {
                    file?.Write(frame, (long)Milliseconds(presented - firstPresent));
                }
            }

            Report(output, stats, totalBytes, options.Seconds, startsCorrectly, pointerOnly, reopened);
            loopback?.Finish(output);
            if (sender is not null)
            {
                output.WriteLine($"Sent {stats.Count} frames to {options.Send} in {sender.PacketsSent:N0} datagrams ({sender.BytesSent:N0} bytes).");
            }
            if (path is not null && stats.Count > 0)
            {
                output.WriteLine($"Wrote {path}");
            }

            return stats.Count > 0 ? 0 : 1;
        }
        finally
        {
            duplication?.Dispose();
            Dxgi.ReleaseCom(immediate);
            Dxgi.ReleaseCom(device);
        }
    }

    /// <summary>
    /// Whether AMF's BGRA surfaces are single textures of the desktop's size and format, so a frame can be copied into
    /// one whole. Says what differs when they aren't.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static bool SurfacesMatch(TextWriter output, AmfContext context, DesktopDuplication duplication)
    {
        nint surface = context.AllocSurface(Amf.SurfaceBgra, duplication.Width, duplication.Height);
        try
        {
            var desc = Dxgi.TextureDesc(AmfContext.SurfaceTexture(surface));
            if (desc.Width == duplication.Width && desc.Height == duplication.Height && desc.Format == duplication.Format && desc.ArraySize == 1)
            {
                return true;
            }

            output.WriteLine($"AMF's surface is {desc.Width}x{desc.Height}, format {desc.Format}, {desc.ArraySize} slice(s); the desktop is"
                + $" {duplication.Width}x{duplication.Height}, format {duplication.Format}. Can't copy one into the other.");
            return false;
        }
        finally
        {
            AmfCom.Release(surface);
        }
    }

    /// <summary>Opens the duplication again after access was lost, retrying until <paramref name="deadline"/>.</summary>
    [SupportedOSPlatform("windows")]
    static DesktopDuplication? Reopen(nint device, DisplayOutput monitor, long deadline)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                return DesktopDuplication.Open(device, monitor);
            }
            catch (COMException)
            {
                Thread.Sleep(100);
            }
        }

        return null;
    }

    /// <summary>
    /// Sends each encoded frame over plain UDP to a receiver on 127.0.0.1 in this process, which puts it back together:
    /// proves the packetizing end to end and times what it adds, before there's a client to send to.
    /// </summary>
    sealed class Loopback : IDisposable
    {
        readonly UdpMediaReceiver _receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly UdpMediaSender _sender;
        readonly Thread _thread;
        readonly ConcurrentDictionary<uint, long> _sentAt = new();
        readonly List<double> _transit = [];
        readonly ManualResetEventSlim _allArrived = new();
        uint _sent;

        public Loopback(Action<AssembledFrame> onFrame)
        {
            _sender = new UdpMediaSender(_receiver.LocalEndPoint);
            _thread = new Thread(() => _receiver.Run(frame =>
            {
                long now = Stopwatch.GetTimestamp();
                if (_sentAt.TryGetValue(frame.FrameNumber, out long sent))
                {
                    _transit.Add(Milliseconds(now - sent));
                }

                onFrame(frame);
                if (frame.FrameNumber == Volatile.Read(ref _sent))
                {
                    _allArrived.Set();
                }
            })) { IsBackground = true, Name = "Loopback receiver" };
            _thread.Start();
        }

        public void Send(byte[] frame, ulong captureMicroseconds, bool recovery)
        {
            uint number = _sent + 1;
            _sentAt[number] = Stopwatch.GetTimestamp();
            _allArrived.Reset();
            Volatile.Write(ref _sent, number);
            _sender.SendFrame(frame, new MediaPacketHeader(MediaKind.Video, recovery ? MediaPacketFlags.Recovery : MediaPacketFlags.None, 0, number, 0, 0, captureMicroseconds));
        }

        /// <summary>Waits briefly for the last frame, stops the receiver and reports what crossed.</summary>
        public void Finish(TextWriter output)
        {
            if (_sent > 0)
            {
                _allArrived.Wait(TimeSpan.FromMilliseconds(500));
            }

            _receiver.Dispose();
            _thread.Join();
            var assembler = _receiver.Assembler;
            output.WriteLine($"Loopback: {assembler.FramesCompleted} of {_sent} frames back whole in {_sender.PacketsSent:N0} datagrams ({_sender.BytesSent:N0} bytes)"
                + $", {assembler.FramesDropped} dropped, {assembler.PacketsRejected} datagrams rejected, {assembler.PacketsDuplicated} duplicated.");
            if (_transit.Count > 0)
            {
                output.WriteLine($"  send to reassembled  {Spread(_transit)}");
            }
        }

        public void Dispose()
        {
            _receiver.Dispose();
            _sender.Dispose();
            _allArrived.Dispose();
        }
    }

    readonly record struct FrameTiming(double PresentToCapture, double CaptureToEncoded, int Bytes)
    {
        public double Total => PresentToCapture + CaptureToEncoded;
    }

    static void Report(TextWriter output, List<FrameTiming> stats, long totalBytes, int seconds, bool startsCorrectly, int pointerOnly, int reopened)
    {
        output.WriteLine();
        output.WriteLine($"{stats.Count} frames in {seconds} s ({stats.Count / (double)seconds:0.0} fps), {pointerOnly} pointer-only updates"
            + (reopened > 0 ? $", duplication reopened {reopened} time(s)" : ""));
        if (stats.Count == 0)
        {
            output.WriteLine("Nothing changed on the monitor, so there was nothing to encode.");
            return;
        }

        // The first frame includes the encoder warming up; it's reported on its own.
        var first = stats[0];
        output.WriteLine($"First frame: {first.Bytes:N0} bytes, present to capture {first.PresentToCapture:0.00} ms, capture to bitstream {first.CaptureToEncoded:0.00} ms"
            + $", {(startsCorrectly ? "starts correctly" : "DOES NOT START CORRECTLY")}");
        var rest = stats.Skip(1).ToList();
        if (rest.Count > 0)
        {
            output.WriteLine($"Then, median / 95th percentile / worst:");
            output.WriteLine($"  present to capture    {Spread(rest.Select(s => s.PresentToCapture))}");
            output.WriteLine($"  capture to bitstream  {Spread(rest.Select(s => s.CaptureToEncoded))}");
            output.WriteLine($"  present to bitstream  {Spread(rest.Select(s => s.Total))}");
            output.WriteLine($"  frame size            {Percentile(rest.Select(s => (double)s.Bytes), 0.5):N0} / {Percentile(rest.Select(s => (double)s.Bytes), 0.95):N0} / {rest.Max(s => s.Bytes):N0} bytes");
        }

        output.WriteLine($"Average {totalBytes * 8.0 / seconds / 1_000_000:0.0} Mbps over the run.");
    }

    static string Spread(IEnumerable<double> values)
    {
        var list = values.ToList();
        return $"{Percentile(list, 0.5):0.00} / {Percentile(list, 0.95):0.00} / {list.Max():0.00} ms";
    }

    /// <summary>The nearest-rank percentile of <paramref name="values"/>.</summary>
    internal static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToList();
        int rank = (int)Math.Ceiling(fraction * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    static string Name(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "HEVC",
        _ => "AV1",
    };
}

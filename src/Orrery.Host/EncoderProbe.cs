using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Orrery.Host.Encoding.Amf;
using Orrery.Host.Encoding.Nvenc;
using Orrery.Host.Platform.Windows;

namespace Orrery.Host;

/// <summary>
/// <c>OrreryHost --probe</c>: lists this machine's GPUs and what each can encode, so a host can be checked before the
/// streaming parts exist. NVIDIA GPUs report NVENC's caps; AMD GPUs report AMF's and encode a few blank frames per codec,
/// which proves the encoder runs and roughly how fast.
/// </summary>
static class EncoderProbe
{
    public static int Run(TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("The probe runs on Windows only so far.");
            return 1;
        }

        var nvenc = NvEncApi.TryLoad(out var nvencMissing);
        output.WriteLine(nvenc is null
            ? $"NVENC: unavailable, {nvencMissing}."
            : $"NVENC: driver supports API {NvEncApi.FormatVersion(nvenc.DriverVersion)}; this build uses {NvEncApi.FormatVersion(NvEncApi.RequiredVersion)}.");
        var amf = AmfRuntime.TryLoad(out var amfMissing);
        output.WriteLine(amf is null
            ? $"AMF: unavailable, {amfMissing}."
            : $"AMF: driver runtime {AmfRuntime.FormatVersion(amf.RuntimeVersion)}; this build uses {AmfRuntime.FormatVersion(Amf.FullVersion)}.");

        foreach (var adapter in Dxgi.Adapters())
        {
            if (adapter.Software)
            {
                continue;
            }

            output.WriteLine();
            output.WriteLine($"{adapter.Index}: {adapter.Name} ({adapter.Vendor}, {adapter.DedicatedVideoMemory / (1024 * 1024)} MB)");
            switch (adapter.VendorId)
            {
                case GpuAdapter.Nvidia when nvenc is not null:
                    ProbeNvenc(output, nvenc, adapter);
                    break;
                case GpuAdapter.Amd when amf is not null:
                    ProbeAmf(output, amf, adapter);
                    break;
                case GpuAdapter.Nvidia or GpuAdapter.Amd:
                    output.WriteLine("   Its encoder is unavailable (see above).");
                    break;
                default:
                    output.WriteLine("   No encoder support yet.");
                    break;
            }
        }

        return 0;
    }

    [SupportedOSPlatform("windows")]
    static void ProbeNvenc(TextWriter output, NvEncApi api, GpuAdapter adapter)
    {
        nint device = 0;
        try
        {
            device = Dxgi.CreateDevice(adapter.Index);
            using var session = NvEncSession.Open(api, device);
            foreach (var codec in session.Codecs())
            {
                var caps = session.Caps(codec);
                output.WriteLine($"   {NvEncSession.CodecName(codec),-5} up to {caps.MaxWidth}x{caps.MaxHeight}, {caps.Engines} engine(s)"
                    + $", 10-bit {YesNo(caps.TenBit)}, 4:4:4 {YesNo(caps.Yuv444)}"
                    + $", intra refresh {YesNo(caps.IntraRefresh)}, reference invalidation {YesNo(caps.RefPicInvalidation)}");
            }
        }
        catch (Exception e) when (e is NvEncException or COMException)
        {
            output.WriteLine($"   NVENC: {e.Message}");
        }
        finally
        {
            Dxgi.ReleaseCom(device);
        }
    }

    [SupportedOSPlatform("windows")]
    static void ProbeAmf(TextWriter output, AmfRuntime runtime, GpuAdapter adapter)
    {
        nint device = 0;
        try
        {
            device = Dxgi.CreateDevice(adapter.Index);
            using var context = runtime.CreateContext(device);
            foreach (var codec in AmfCodec.All)
            {
                string name = CodecName(codec.Codec);
                try
                {
                    using var encoder = context.CreateComponent(codec.ComponentId);
                    var caps = encoder.Caps(codec.CapMaxBitrate, codec.CapHardwareInstances);
                    output.WriteLine($"   {name,-5} up to {caps.MaxWidth}x{caps.MaxHeight}, {caps.HardwareInstances} instance(s)"
                        + $", max {caps.MaxBitrate / 1_000_000} Mbps{(caps.Hardware ? "" : ", not hardware")}");
                    foreach (var (width, height) in TestSizes)
                    {
                        TestEncode(output, context, codec, width, height);
                    }
                }
                catch (AmfException e) when (e.Result is AMF_RESULT.AMF_CODEC_NOT_SUPPORTED or AMF_RESULT.AMF_ENCODER_NOT_PRESENT or AMF_RESULT.AMF_NOT_SUPPORTED)
                {
                    output.WriteLine($"   {name,-5} not supported ({e.Result})");
                }
                catch (AmfException e)
                {
                    output.WriteLine($"   {name,-5} {e.Message}");
                }
            }
        }
        catch (Exception e) when (e is AmfException or COMException)
        {
            output.WriteLine($"   AMF: {e.Message}");
        }
        finally
        {
            Dxgi.ReleaseCom(device);
        }
    }

    static readonly (int Width, int Height)[] TestSizes = [(1920, 1080), (3840, 2160)];

    /// <summary>
    /// Encodes blank frames with a fresh encoder at one size. Blank frames are the easiest there are, so real content
    /// takes longer; this shows the encoder runs and the floor of its latency.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static void TestEncode(TextWriter output, AmfContext context, AmfCodec codec, int width, int height)
    {
        try
        {
            using var encoder = context.CreateComponent(codec.ComponentId);
            var test = AmfTestEncoder.Run(context, encoder, codec, width, height);
            output.WriteLine($"         {test.Width}x{test.Height}, {test.Frames} blank frames: first {test.FirstMilliseconds:0.0} ms, then median {test.MedianMilliseconds:0.00} ms"
                + $"; first frame {test.FirstFrameBytes:N0} bytes, {(test.StreamStartsCorrectly ? "starts correctly" : "DOES NOT START CORRECTLY")}");
        }
        catch (AmfException e)
        {
            output.WriteLine($"         {width}x{height}: {e.Message}");
        }
    }

    static string CodecName(Orrery.Streaming.VideoCodec codec) => codec switch
    {
        Orrery.Streaming.VideoCodec.H264 => "H.264",
        Orrery.Streaming.VideoCodec.Hevc => "HEVC",
        _ => "AV1",
    };

    static string YesNo(bool value) => value ? "yes" : "no";
}

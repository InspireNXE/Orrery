using System.Diagnostics;
using System.Runtime.Versioning;
using Orrery.Streaming;

namespace Orrery.Host.Encoding.Amf;

/// <summary>How one AMF encoder is named and configured: each codec has its own component and property names.</summary>
sealed record AmfCodec(
    VideoCodec Codec,
    string ComponentId,
    string Usage,
    long UltraLowLatency,
    string FrameSize,
    string FrameRate,
    string TargetBitrate,
    string QueryTimeout,
    string CapMaxBitrate,
    string CapHardwareInstances)
{
    public static IReadOnlyList<AmfCodec> All { get; } =
    [
        new(VideoCodec.H264, Amf.EncoderAvc, Amf.AvcUsage, Amf.AvcUsageUltraLowLatency, Amf.AvcFrameSize, Amf.AvcFrameRate,
            Amf.AvcTargetBitrate, Amf.AvcQueryTimeout, Amf.AvcCapMaxBitrate, Amf.AvcCapNumOfHwInstances),
        new(VideoCodec.Hevc, Amf.EncoderHevc, Amf.HevcUsage, Amf.HevcUsageUltraLowLatency, Amf.HevcFrameSize, Amf.HevcFrameRate,
            Amf.HevcTargetBitrate, Amf.HevcQueryTimeout, Amf.HevcCapMaxBitrate, Amf.HevcCapNumOfHwInstances),
        new(VideoCodec.Av1, Amf.EncoderAv1, Amf.Av1Usage, Amf.Av1UsageUltraLowLatency, Amf.Av1FrameSize, Amf.Av1FrameRate,
            Amf.Av1TargetBitrate, Amf.Av1QueryTimeout, Amf.Av1CapMaxBitrate, Amf.Av1CapNumOfHwInstances),
    ];
}

/// <summary>The result of encoding a few blank frames: proof the encoder runs, and roughly how fast.</summary>
sealed record AmfTestEncode(int Frames, int Width, int Height, double FirstMilliseconds, double MedianMilliseconds, int FirstFrameBytes, bool StreamStartsCorrectly);

[SupportedOSPlatform("windows")]
static class AmfTestEncoder
{
    /// <summary>
    /// Sets <paramref name="encoder"/> up for ultra-low-latency streaming at <paramref name="width"/> x
    /// <paramref name="height"/>, encodes <paramref name="frames"/> uninitialized NV12 surfaces one at a time, waiting for
    /// each frame's bitstream before submitting the next, and times submit-to-output.
    /// </summary>
    public static AmfTestEncode Run(AmfContext context, AmfComponent encoder, AmfCodec codec, int width = 1280, int height = 720, int frames = 30)
    {
        encoder.SetInt64(codec.Usage, codec.UltraLowLatency);
        if (codec.Codec == VideoCodec.Av1)
        {
            encoder.SetInt64(Amf.Av1EncodingLatencyMode, Amf.Av1EncodingLatencyModeLowest);
        }

        encoder.SetSize(codec.FrameSize, width, height);
        encoder.SetRate(codec.FrameRate, 60, 1);
        encoder.SetInt64(codec.TargetBitrate, 20_000_000);
        // QueryOutput waits up to this long for a frame instead of returning AMF_REPEAT at once.
        encoder.SetInt64(codec.QueryTimeout, 1000);
        encoder.Init(Amf.SurfaceNv12, width, height);

        var times = new List<double>(frames);
        byte[]? first = null;
        for (int i = 0; i < frames; i++)
        {
            nint surface = context.AllocSurface(Amf.SurfaceNv12, width, height);
            try
            {
                long start = Stopwatch.GetTimestamp();
                AmfCom.Check("AMFComponent::SubmitInput", encoder.SubmitInput(surface));
                nint output;
                AMF_RESULT result;
                while ((result = encoder.QueryOutput(out output)) == AMF_RESULT.AMF_REPEAT)
                {
                }

                AmfCom.Check("AMFComponent::QueryOutput", result);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                try
                {
                    first ??= AmfComponent.ReadBuffer(output);
                }
                finally
                {
                    AmfCom.Release(output);
                }
            }
            finally
            {
                AmfCom.Release(surface);
            }
        }

        var sorted = times.Skip(1).Order().ToList();
        return new AmfTestEncode(frames, width, height, times[0], sorted.Count > 0 ? sorted[sorted.Count / 2] : times[0],
            first?.Length ?? 0, first is not null && StartsCorrectly(codec.Codec, first));
    }

    /// <summary>
    /// Whether a first frame begins the way its codec's stream must: an Annex B start code for H.264 and HEVC, a
    /// temporal delimiter OBU for AV1.
    /// </summary>
    public static bool StartsCorrectly(VideoCodec codec, ReadOnlySpan<byte> frame) => codec switch
    {
        VideoCodec.Av1 => frame.Length >= 2 && frame[0] == 0x12 && frame[1] == 0x00,
        _ => frame.StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 1]) || frame.StartsWith((ReadOnlySpan<byte>)[0, 0, 1]),
    };
}

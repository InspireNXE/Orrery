using System.Buffers.Binary;

namespace Orrery.Core.Recording;

/// <summary>The sound track of an export: 16-bit PCM at one rate and channel count.</summary>
public sealed record SoundFormat(int SampleRate, int Channels)
{
    public int BlockAlign => Channels * 2;

    public int BytesPerSecond => SampleRate * BlockAlign;

    /// <summary>
    /// The format a recording's sound exports in, from its first sound, or null when it has none: 44.1 or 48 kHz as
    /// recorded, else 48 kHz; mono or stereo, more channels mixed down to two. Every MP4 encoder takes these.
    /// </summary>
    public static SoundFormat? Of(RecordingReader reader, CancellationToken cancel = default)
    {
        foreach (var e in reader.Events())
        {
            cancel.ThrowIfCancellationRequested();
            if (e.Type == RecordingEventType.Audio && RecordingFile.ReadAudio(e.Payload) is var (rate, channels, _))
            {
                return new SoundFormat(rate is 44_100 or 48_000 ? rate : 48_000, Math.Min(channels, 2));
            }
        }

        return null;
    }
}

/// <summary>
/// Lays a recording's sound out on the export's clock (design §3.4). Each piece of sound was stamped when the recorder
/// took it, a little after it played, and pieces taken together share a stamp; so a piece that would start inside the
/// sound already laid follows on from it, and only a gap longer than <see cref="Slack"/> is filled with silence.
/// </summary>
public sealed class SoundTrack(SoundFormat format)
{
    /// <summary>Gaps up to this are taken for the recorder's timing, not silence.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(120);

    readonly MemoryStream _pending = new();
    long _taken; // sample frames handed out

    public SoundFormat Format { get; } = format;

    /// <summary>Sample frames laid so far, handed out or not.</summary>
    public long Length => _taken + _pending.Length / Format.BlockAlign;

    /// <summary>Lays a recorded <see cref="RecordingEventType.Audio"/> event at <paramref name="at"/> on the export's clock.</summary>
    public void Add(TimeSpan at, byte[] payload)
    {
        if (RecordingFile.ReadAudio(payload) is not var (rate, channels, pcm))
        {
            return;
        }

        long start = (long)(at.TotalSeconds * Format.SampleRate);
        long gap = start - Length;
        if (gap > (long)(Slack.TotalSeconds * Format.SampleRate))
        {
            _pending.Write(new byte[gap * Format.BlockAlign]);
        }

        var converted = Convert(pcm.Span, rate, channels, Format);
        _pending.Write(converted);
    }

    /// <summary>The sound laid since the last call, in order; empty when there's none.</summary>
    public byte[] Take()
    {
        if (_pending.Length == 0)
        {
            return [];
        }

        var bytes = _pending.ToArray();
        _pending.SetLength(0);
        _taken += bytes.Length / Format.BlockAlign;
        return bytes;
    }

    /// <summary>Converts 16-bit PCM to <paramref name="to"/>: channels are mixed or copied, and the rate resampled linearly.</summary>
    public static byte[] Convert(ReadOnlySpan<byte> pcm, int rate, int channels, SoundFormat to)
    {
        int frames = pcm.Length / (channels * 2);
        if (rate == to.SampleRate && channels == to.Channels)
        {
            return pcm[..(frames * channels * 2)].ToArray();
        }

        // First to the wanted channels, as floats.
        var mixed = new float[frames * to.Channels];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < to.Channels; c++)
            {
                float value;
                if (to.Channels == 1)
                {
                    float sum = 0;
                    for (int s = 0; s < channels; s++)
                    {
                        sum += Sample(pcm, f * channels + s);
                    }

                    value = sum / channels;
                }
                else
                {
                    // Stereo from mono doubles it; from more channels, the first two are front left and right.
                    value = Sample(pcm, f * channels + Math.Min(c, channels - 1));
                }

                mixed[f * to.Channels + c] = value;
            }
        }

        int outFrames = rate == to.SampleRate ? frames : (int)((long)frames * to.SampleRate / rate);
        var output = new byte[outFrames * to.BlockAlign];
        for (int f = 0; f < outFrames; f++)
        {
            double position = rate == to.SampleRate ? f : (double)f * rate / to.SampleRate;
            int i = (int)position;
            double t = position - i;
            int j = Math.Min(i + 1, frames - 1);
            for (int c = 0; c < to.Channels; c++)
            {
                double value = mixed[i * to.Channels + c] * (1 - t) + mixed[j * to.Channels + c] * t;
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan((f * to.Channels + c) * 2), (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
            }
        }

        return output;
    }

    static float Sample(ReadOnlySpan<byte> pcm, int index) => BinaryPrimitives.ReadInt16LittleEndian(pcm[(index * 2)..]);
}

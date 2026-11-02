using Orrery.Core.Recording;
using Xunit;

namespace Orrery.Core.Tests.Recording;

public sealed class SoundStretchTests
{
    const int Rate = 48_000;

    static byte[] Tone(double hertz, TimeSpan length, int channels = 2)
    {
        int frames = (int)(Rate * length.TotalSeconds);
        var pcm = new byte[frames * channels * 2];
        for (int f = 0; f < frames; f++)
        {
            short s = (short)(8000 * Math.Sin(2 * Math.PI * hertz * f / Rate));
            for (int c = 0; c < channels; c++)
            {
                int at = (f * channels + c) * 2;
                pcm[at] = (byte)s;
                pcm[at + 1] = (byte)(s >> 8);
            }
        }

        return pcm;
    }

    /// <summary>Stretches the sound in 40 ms chunks, as the player gets it.</summary>
    static byte[] Stretched(byte[] pcm, double speed, int channels = 2)
    {
        var stretch = new SoundStretch(Rate, channels, speed);
        var output = new List<byte>();
        int chunk = Rate / 25 * channels * 2;
        for (int at = 0; at < pcm.Length; at += chunk)
        {
            output.AddRange(stretch.Stretch(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at))));
        }

        return output.ToArray();
    }

    /// <summary>The pitch, from how often the first channel crosses zero going up, skipping the fade in.</summary>
    static double Hertz(byte[] pcm, int channels = 2)
    {
        int frames = pcm.Length / 2 / channels, first = Rate / 10, crossings = 0;
        short previous = 0;
        for (int f = first; f < frames; f++)
        {
            short s = (short)(pcm[f * channels * 2] | pcm[f * channels * 2 + 1] << 8);
            if (previous < 0 && s >= 0)
            {
                crossings++;
            }

            previous = s;
        }

        return crossings / ((double)(frames - first) / Rate);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2)]
    [InlineData(4)]
    public void Sound_lasts_as_long_as_the_speed_says_at_the_same_pitch(double speed)
    {
        var tone = Tone(440, TimeSpan.FromSeconds(4));
        var output = Stretched(tone, speed);

        double seconds = output.Length / 4.0 / Rate;
        Assert.InRange(seconds, 4 / speed - 0.1, 4 / speed + 0.01); // it holds back a little, never more than it's given
        Assert.InRange(Hertz(output), 430, 450);
    }

    [Fact]
    public void A_steady_tone_comes_out_as_loud_as_it_went_in()
    {
        var output = Stretched(Tone(440, TimeSpan.FromSeconds(2), channels: 1), 2, channels: 1);
        short peak = 0;
        for (int i = Rate / 10 * 2; i + 1 < output.Length; i += 2)
        {
            peak = Math.Max(peak, (short)(output[i] | output[i + 1] << 8));
        }

        Assert.InRange(peak, 7000, 8500);
    }
}

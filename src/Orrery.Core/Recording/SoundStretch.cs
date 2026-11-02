namespace Orrery.Core.Recording;

/// <summary>
/// Plays sound faster or slower without changing its pitch, as a browser does. It's cut into overlapping pieces that
/// are faded into one another a fixed step apart while the place each is taken from moves on by the step times the
/// speed; each piece is nudged to where it lines up best with the one before (WSOLA), so voices don't warble. 16-bit
/// PCM, interleaved, a chunk at a time; it holds back about a piece and a half of sound.
/// </summary>
public sealed class SoundStretch
{
    /// <summary>How long a piece is.</summary>
    static readonly TimeSpan Piece = TimeSpan.FromMilliseconds(40);

    /// <summary>How far either way a piece may be nudged to line up.</summary>
    static readonly TimeSpan Nudge = TimeSpan.FromMilliseconds(12);

    readonly int _channels;
    readonly int _half, _search;
    readonly float[] _window;
    readonly float[] _tail;
    float[] _input = new float[16_384];
    int _frames;
    double _next;
    int _carryOn = -1;

    public SoundStretch(int sampleRate, int channels, double speed)
    {
        if (sampleRate <= 0 || channels <= 0 || speed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        SampleRate = sampleRate;
        _channels = channels;
        Speed = speed;
        _half = Math.Max(16, (int)(sampleRate * Piece.TotalSeconds / 2));
        _search = (int)(sampleRate * Nudge.TotalSeconds);
        _window = new float[_half * 2];
        for (int i = 0; i < _window.Length; i++)
        {
            // A periodic Hann window: two of them half a piece apart add up to exactly one.
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _window.Length));
        }

        _tail = new float[_half * channels];
    }

    public int SampleRate { get; }

    public int Channels => _channels;

    public double Speed { get; }

    /// <summary>Takes a chunk of sound and returns what's ready to play, which may be nothing yet.</summary>
    public byte[] Stretch(ReadOnlySpan<byte> pcm)
    {
        Append(pcm);
        int piece = _half * 2;
        var output = new List<byte>();
        while ((int)_next + _search + piece <= _frames)
        {
            int at = _carryOn < 0 ? (int)_next : BestMatch((int)_next);
            for (int i = 0; i < _half; i++)
            {
                for (int c = 0; c < _channels; c++)
                {
                    float sample = _tail[i * _channels + c] + _input[(at + i) * _channels + c] * _window[i];
                    short s = (short)Math.Clamp(MathF.Round(sample), short.MinValue, short.MaxValue);
                    output.Add((byte)s);
                    output.Add((byte)(s >> 8));
                    _tail[i * _channels + c] = _input[(at + _half + i) * _channels + c] * _window[_half + i];
                }
            }

            _carryOn = at + _half;
            _next += _half * Speed;
            Trim();
        }

        return output.ToArray();
    }

    void Append(ReadOnlySpan<byte> pcm)
    {
        int frames = pcm.Length / 2 / _channels;
        if ((_frames + frames) * _channels > _input.Length)
        {
            Array.Resize(ref _input, Math.Max(_input.Length * 2, (_frames + frames) * _channels));
        }

        for (int i = 0, o = _frames * _channels; i < frames * _channels; i++, o++)
        {
            _input[o] = (short)(pcm[i * 2] | pcm[i * 2 + 1] << 8);
        }

        _frames += frames;
    }

    /// <summary>
    /// The start near <paramref name="around"/> whose opening sounds most like how the last piece carried on, by
    /// correlation of the channels mixed, every other sample and every other place to keep it cheap.
    /// </summary>
    int BestMatch(int around)
    {
        int best = around;
        double bestScore = double.NegativeInfinity;
        for (int at = Math.Max(0, around - _search); at <= around + _search; at += 2)
        {
            double dot = 0, energy = 1e-9;
            for (int i = 0; i < _half; i += 2)
            {
                float a = Mixed(at + i), b = Mixed(_carryOn + i);
                dot += a * b;
                energy += a * a;
            }

            double score = dot / Math.Sqrt(energy);
            if (score > bestScore)
            {
                bestScore = score;
                best = at;
            }
        }

        return best;
    }

    float Mixed(int frame)
    {
        float sum = 0;
        for (int c = 0; c < _channels; c++)
        {
            sum += _input[frame * _channels + c];
        }

        return sum;
    }

    /// <summary>Drops the sound no later piece can start in.</summary>
    void Trim()
    {
        int keep = Math.Min(Math.Max(0, (int)_next - _search), _carryOn);
        if (keep < 4096)
        {
            return;
        }

        Array.Copy(_input, keep * _channels, _input, 0, (_frames - keep) * _channels);
        _frames -= keep;
        _next -= keep;
        _carryOn -= keep;
    }
}

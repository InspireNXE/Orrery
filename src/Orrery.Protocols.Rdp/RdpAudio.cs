using Devolutions.IronRdp;
using Orrery.Core.Sessions;
using AudioChunk = Orrery.Core.Sessions.AudioChunk;

namespace Orrery.Protocols.Rdp;

/// <summary>
/// The native library's copy of the session's decoded sound (IronRDP fork's <c>AudioTap</c>). The native side is freed
/// with the last connection that holds it, so disposing only stops it.
/// </summary>
public sealed class RdpAudioTap : IAudioTap, IDisposable
{
    readonly object _lock = new();
    AudioTap? _tap = AudioTap.New();

    /// <summary>The native tap, for the connector; shared by every connection the session makes.</summary>
    internal AudioTap Native => _tap ?? throw new ObjectDisposedException(nameof(RdpAudioTap));

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _tap?.SetEnabled(enabled);
        }
    }

    public AudioChunk? TryTake()
    {
        lock (_lock)
        {
            var chunk = _tap?.Pop();
            if (chunk is null)
            {
                return null;
            }

            var pcm = new byte[(int)chunk.GetSize()];
            chunk.Fill(pcm);
            return new AudioChunk((int)chunk.GetSampleRate(), chunk.GetChannels(), pcm);
        }
    }

    public bool Muted
    {
        get
        {
            lock (_lock)
            {
                return _tap?.IsMuted() ?? false;
            }
        }
        set
        {
            lock (_lock)
            {
                _tap?.SetMuted(value);
            }
        }
    }

    public TimeSpan? SoundAge
    {
        get
        {
            lock (_lock)
            {
                return _tap?.GetSoundAgeMicros() is { } micros and not ulong.MaxValue ? TimeSpan.FromMicroseconds(micros) : null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _tap?.SetEnabled(false);
            _tap = null;
        }
    }
}

/// <summary>Plays PCM through the native library's output (IronRDP fork's <c>AudioOutput</c>, CPAL underneath).</summary>
public sealed class RdpAudioOutput : IAudioOutput
{
    readonly object _lock = new();
    AudioOutput? _output;

    RdpAudioOutput(AudioOutput output) => _output = output;

    /// <summary>Opens the default output device; null when there's none or the native library can't play sound.</summary>
    public static IAudioOutput? TryOpen(int sampleRate, int channels)
    {
        try
        {
            return new RdpAudioOutput(AudioOutput.Open((uint)sampleRate, (ushort)channels));
        }
        catch (IronRdpException)
        {
            return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    public TimeSpan Buffered
    {
        get
        {
            lock (_lock)
            {
                return _output is null ? TimeSpan.Zero : TimeSpan.FromMicroseconds(_output.GetBufferedMicros());
            }
        }
    }

    public int Write(ReadOnlySpan<byte> pcm)
    {
        lock (_lock)
        {
            return _output is null || pcm.IsEmpty ? 0 : (int)_output.Write(pcm.ToArray());
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _output?.Clear();
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _output?.SetPaused(paused);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _output?.Close();
            _output = null;
        }
    }
}

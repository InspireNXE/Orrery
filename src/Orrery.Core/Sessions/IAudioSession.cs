namespace Orrery.Core.Sessions;

/// <summary>A session that plays the remote computer's sound here and can hand a copy of it to a recording (RDP).</summary>
public interface IAudioSession
{
    /// <summary>The copy of the sound; null when the session doesn't play sound.</summary>
    IAudioTap? AudioTap { get; }
}

/// <summary>
/// Decoded sound as it's played. Nothing is kept until it's enabled; then it keeps a few seconds, dropping the oldest
/// when it isn't taken in time.
/// </summary>
public interface IAudioTap
{
    /// <summary>Starts or stops keeping sound; stopping discards what's kept.</summary>
    void SetEnabled(bool enabled);

    /// <summary>The oldest sound kept, or null.</summary>
    AudioChunk? TryTake();
}

/// <summary>Interleaved 16-bit little-endian PCM.</summary>
public sealed record AudioChunk(int SampleRate, int Channels, byte[] Pcm);

/// <summary>Plays 16-bit PCM on this computer's default output device.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Queues interleaved samples; returns how many bytes fit.</summary>
    int Write(ReadOnlySpan<byte> pcm);

    /// <summary>Sound queued and not yet played.</summary>
    TimeSpan Buffered { get; }

    /// <summary>Discards what's queued.</summary>
    void Clear();

    /// <summary>Plays silence while paused, keeping what's queued.</summary>
    void SetPaused(bool paused);
}

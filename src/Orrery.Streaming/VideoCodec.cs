namespace Orrery.Streaming;

/// <summary>A video codec OSP can carry. Both sides advertise what their hardware can do; see <see cref="VideoCodecs.Choose"/>.</summary>
public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
}

public static class VideoCodecs
{
    /// <summary>Best first, as the client picks when the connection doesn't name one.</summary>
    public static IReadOnlyList<VideoCodec> Preference { get; } = [VideoCodec.Av1, VideoCodec.Hevc, VideoCodec.H264];

    /// <summary>
    /// The codec to stream: <paramref name="wanted"/> if both sides have it, otherwise the best both have, or null when
    /// they share none.
    /// </summary>
    public static VideoCodec? Choose(IReadOnlySet<VideoCodec> host, IReadOnlySet<VideoCodec> client, VideoCodec? wanted = null)
    {
        if (wanted is { } codec && host.Contains(codec) && client.Contains(codec))
        {
            return codec;
        }

        foreach (var candidate in Preference)
        {
            if (host.Contains(candidate) && client.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

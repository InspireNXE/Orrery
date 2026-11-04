using Orrery.Streaming;

namespace Orrery.Protocols.Stream;

/// <summary>Whether the mouse moves relatively with the pointer locked, as games want, or absolutely, as a desktop does.</summary>
public enum RelativeMouse
{
    /// <summary>Follows the host: relative while a game hides the cursor.</summary>
    Auto,
    On,
    Off,
}

/// <summary>Where the host's sound plays.</summary>
public enum StreamAudio
{
    /// <summary>On this computer, with the host's speakers silenced while streaming.</summary>
    PlayHere,
    PlayOnHost,
    Both,
    Off,
}

/// <summary>
/// What an OSP connection asks of the host, from the properties pane (docs/design-stream.md §9). Not read from a
/// connection yet: OSP isn't in Orrery's protocol list until phase 3.
/// </summary>
public sealed record StreamOptions
{
    /// <summary>The codec to use when both sides have it; null picks the best they share.</summary>
    public VideoCodec? Codec { get; init; }

    /// <summary>Frames per second to ask for; null matches the client's display.</summary>
    public int? FrameRate { get; init; }

    /// <summary>The most the stream may use, in kilobits per second; null lets rate control decide.</summary>
    public int? MaxBitrateKbps { get; init; }

    public RelativeMouse RelativeMouse { get; init; } = RelativeMouse.Auto;

    /// <summary>Send this computer's game controllers to the host.</summary>
    public bool Controllers { get; init; } = true;

    public StreamAudio Audio { get; init; } = StreamAudio.PlayHere;

    /// <summary>Send this computer's microphone to the host. Off unless asked for.</summary>
    public bool Microphone { get; init; }

    public bool ShareClipboard { get; init; } = true;
}

using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

/// <summary>
/// One protocol as the app shows it: its badge and what its sessions can't change while open. What the protocol is,
/// without drawing it, is in <see cref="Traits"/> from Orrery.Core. See docs/design-protocol-registry.md.
/// </summary>
abstract class ProtocolDescriptor
{
    /// <param name="protocols">Usually one; web is HTTP and HTTPS, stored as two.</param>
    protected ProtocolDescriptor(params Protocol[] protocols)
    {
        Traits = [.. protocols.Select(ProtocolTraits.Of)];
    }

    /// <summary>The protocols this describes, in the order the protocol picker lists them.</summary>
    public IReadOnlyList<ProtocolTraits> Traits { get; }

    /// <summary>The badge's background.</summary>
    public abstract IBrush Background { get; }

    /// <summary>The badge's text and the protocol's icon.</summary>
    public abstract IBrush Foreground { get; }

    /// <summary>A <see cref="Controls.Icons"/> path: remote desktops get a monitor, terminals a prompt.</summary>
    public abstract string Icon { get; }

    /// <summary>
    /// The saved settings a running session can't take up until it reconnects, as they'd be used then. These are the
    /// ones every session has; a protocol with more lists its own.
    /// </summary>
    public virtual IEnumerable<(string Label, string Value)> FixedSettings(ResolvedSettings saved, bool enlargeByDefault)
    {
        yield return DisplayQuality(saved);
        yield return MatchScaling(saved, enlargeByDefault);
        foreach (var setting in SessionSettings(saved))
        {
            yield return setting;
        }

        if (saved.RecordTyping.Value)
        {
            yield return ("Record what's typed too", "On");
        }
    }

    protected static (string, string) DisplayQuality(ResolvedSettings saved) =>
        ("Display quality", PropertiesPanel.QualityLabels.First(q => q.Quality == saved.DisplayQuality.Value).Label);

    protected static (string, string) MatchScaling(ResolvedSettings saved, bool enlargeByDefault) =>
        ("Match display scaling", OnOff(saved.Enlarge(enlargeByDefault)));

    /// <summary>The clipboard, reconnecting and recording.</summary>
    protected static IEnumerable<(string, string)> SessionSettings(ResolvedSettings saved) =>
    [
        ("Share the clipboard", OnOff(saved.ShareClipboard.Value)),
        ("Reconnect if it drops", OnOff(saved.AutoReconnect.Value)),
        ("Record sessions", OnOff(saved.RecordSessions.Value)),
    ];

    protected static string OnOff(bool on) => on ? "On" : "Off";

    protected static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color)).ToImmutable();
}

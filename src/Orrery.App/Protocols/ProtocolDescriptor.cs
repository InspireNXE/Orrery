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

    /// <summary>What the properties pane calls the connection's host, and what it suggests.</summary>
    public virtual HostField Host => HostField.Network;

    /// <summary>Whether a connection can ask for a fixed remote desktop size (RDP); a VNC server sets its own.</summary>
    public virtual bool ChoosesDesktopSize => false;

    /// <summary>
    /// The protocol's own section in the properties pane, or null when it has none. Shown for its connections, and
    /// for folders and the defaults, whose settings reach connections of every protocol.
    /// </summary>
    public virtual PropertiesSection? CreateSection(PropertiesContext context) => null;

    /// <summary>False for one that can open with no host (a local shell: the one chosen in Settings).</summary>
    public virtual bool NeedsHost => true;

    /// <summary>What to do about a saved connection with no host.</summary>
    public virtual string NoHostHelp => "Enter a host name or IP address in its settings first.";

    /// <summary>Whether it can open a file browser instead of its usual session (SSH's SFTP).</summary>
    public virtual bool OpensFiles => false;

    /// <summary>The ports a session forwards for a saved connection, and the SOCKS proxy its folder asks for.</summary>
    public virtual IReadOnlyList<PortForward> Forwards(ConnectionEntry entry, ResolvedSettings resolved) => [];

    /// <summary>
    /// Opens what isn't a session Orrery logs in to, such as a shell on this computer or a web page, without the
    /// connect steps (jump hosts, the login). False for a protocol that starts with <see cref="StartAsync"/>.
    /// </summary>
    public virtual Task<bool> OpenAsync(ISessionHost host, OpenRequest request) => Task.FromResult(false);

    /// <summary>
    /// Starts a session once the connect steps are done: its jump hosts are reached and its login is known. A
    /// protocol that opens with <see cref="OpenAsync"/> never gets here.
    /// </summary>
    public virtual Task StartAsync(ISessionHost host, StartRequest request) =>
        throw new InvalidOperationException($"{Traits[0].Label} opens without starting a session.");

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

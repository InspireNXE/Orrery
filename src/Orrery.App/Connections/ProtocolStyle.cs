using Avalonia.Media;
using Orrery.App.Protocols;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>The badge colors and icon for each protocol, so a connection reads the same everywhere. Each protocol's descriptor says what they are.</summary>
static class ProtocolStyle
{
    public static IBrush Background(Protocol protocol) => ProtocolRegistry.For(protocol).Background;

    public static IBrush Foreground(Protocol protocol) => ProtocolRegistry.For(protocol).Foreground;

    /// <summary>Remote desktops get a monitor, terminals a prompt, serial ports a connector and web pages a globe.</summary>
    public static string Icon(Protocol protocol) => ProtocolRegistry.For(protocol).Icon;
}

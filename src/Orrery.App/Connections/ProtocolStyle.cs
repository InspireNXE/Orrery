using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>The badge colors and icon for each protocol, so a connection reads the same everywhere.</summary>
static class ProtocolStyle
{
    static readonly IBrush RdpBack = Brush("#1D2E45"), RdpFore = Brush("#8CC2FF");
    static readonly IBrush SshBack = Brush("#183526"), SshFore = Brush("#7EE0A6");
    static readonly IBrush VncBack = Brush("#3A2A17"), VncFore = Brush("#F3B672");
    static readonly IBrush TelnetBack = Brush("#2E2445"), TelnetFore = Brush("#C9B0FF");

    public static IBrush Background(Protocol protocol) => protocol switch
    {
        Protocol.Ssh => SshBack,
        Protocol.Vnc => VncBack,
        Protocol.Telnet => TelnetBack,
        _ => RdpBack,
    };

    public static IBrush Foreground(Protocol protocol) => protocol switch
    {
        Protocol.Ssh => SshFore,
        Protocol.Vnc => VncFore,
        Protocol.Telnet => TelnetFore,
        _ => RdpFore,
    };

    /// <summary>Remote desktops get a monitor; terminals get a prompt.</summary>
    public static string Icon(Protocol protocol) =>
        protocol is Protocol.Ssh or Protocol.Telnet ? Icons.Terminal : Icons.Monitor;

    static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color)).ToImmutable();
}

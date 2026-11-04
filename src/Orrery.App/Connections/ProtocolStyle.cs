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
    static readonly IBrush SerialBack = Brush("#3A2230"), SerialFore = Brush("#F2A7C8");
    static readonly IBrush WebBack = Brush("#173538"), WebFore = Brush("#7FD8DE");
    static readonly IBrush LocalBack = Brush("#33331A"), LocalFore = Brush("#E6E08A");

    public static IBrush Background(Protocol protocol) => protocol switch
    {
        Protocol.Ssh => SshBack,
        Protocol.Vnc => VncBack,
        Protocol.Telnet => TelnetBack,
        Protocol.Serial => SerialBack,
        Protocol.Http or Protocol.Https => WebBack,
        Protocol.Local => LocalBack,
        _ => RdpBack,
    };

    public static IBrush Foreground(Protocol protocol) => protocol switch
    {
        Protocol.Ssh => SshFore,
        Protocol.Vnc => VncFore,
        Protocol.Telnet => TelnetFore,
        Protocol.Serial => SerialFore,
        Protocol.Http or Protocol.Https => WebFore,
        Protocol.Local => LocalFore,
        _ => RdpFore,
    };

    /// <summary>Remote desktops get a monitor, terminals a prompt, serial ports a connector and web pages a globe.</summary>
    public static string Icon(Protocol protocol) => protocol switch
    {
        Protocol.Ssh or Protocol.Telnet or Protocol.Local => Icons.Terminal,
        Protocol.Serial => Icons.SerialPort,
        Protocol.Http or Protocol.Https => Icons.Globe,
        _ => Icons.Monitor,
    };

    static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color)).ToImmutable();
}

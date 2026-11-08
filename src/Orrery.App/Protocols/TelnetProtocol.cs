using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

sealed class TelnetProtocol() : ProtocolDescriptor(Protocol.Telnet)
{
    public override IBrush Background { get; } = Brush("#2E2445");

    public override IBrush Foreground { get; } = Brush("#C9B0FF");

    public override string Icon => Icons.Terminal;
}

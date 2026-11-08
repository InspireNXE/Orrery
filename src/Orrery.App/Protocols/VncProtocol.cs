using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

sealed class VncProtocol() : ProtocolDescriptor(Protocol.Vnc)
{
    public override IBrush Background { get; } = Brush("#3A2A17");

    public override IBrush Foreground { get; } = Brush("#F3B672");

    public override string Icon => Icons.Monitor;

    public override PropertiesSection CreateSection(PropertiesContext context) => new VncSection(context);
}

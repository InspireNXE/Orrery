using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

sealed class SshProtocol() : ProtocolDescriptor(Protocol.Ssh)
{
    public override IBrush Background { get; } = Brush("#183526");

    public override IBrush Foreground { get; } = Brush("#7EE0A6");

    public override string Icon => Icons.Terminal;

    public override PropertiesSection CreateSection(PropertiesContext context) => new SshSection(context);
}

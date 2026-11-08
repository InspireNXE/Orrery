using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

sealed class SerialProtocol() : ProtocolDescriptor(Protocol.Serial)
{
    public override IBrush Background { get; } = Brush("#3A2230");

    public override IBrush Foreground { get; } = Brush("#F2A7C8");

    public override string Icon => Icons.SerialPort;
}

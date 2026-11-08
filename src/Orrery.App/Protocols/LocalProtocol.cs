using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

/// <summary>A shell on this computer.</summary>
sealed class LocalProtocol() : ProtocolDescriptor(Protocol.Local)
{
    public override IBrush Background { get; } = Brush("#33331A");

    public override IBrush Foreground { get; } = Brush("#E6E08A");

    public override string Icon => Icons.Terminal;
}

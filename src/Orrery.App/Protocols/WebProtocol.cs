using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

/// <summary>HTTP and HTTPS: one kind of connection, stored as two protocols.</summary>
sealed class WebProtocol() : ProtocolDescriptor(Protocol.Http, Protocol.Https)
{
    public override IBrush Background { get; } = Brush("#173538");

    public override IBrush Foreground { get; } = Brush("#7FD8DE");

    public override string Icon => Icons.Globe;
}

using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

/// <summary>HTTP and HTTPS: one kind of connection, stored as two protocols.</summary>
sealed class WebProtocol() : ProtocolDescriptor(Protocol.Http, Protocol.Https)
{
    public override IBrush Background { get; } = Brush("#173538");

    public override IBrush Foreground { get; } = Brush("#7FD8DE");

    public override string Icon => Icons.Globe;

    /// <summary>A web page's host can have a path after it.</summary>
    public override HostField Host { get; } = new("Host (and path)", "router.lan/admin");

    /// <summary>In a tab, or in the default browser if the connection or Settings says so; never through a jump host.</summary>
    public override async Task<bool> OpenAsync(ISessionHost host, OpenRequest request)
    {
        await host.OpenWebPageAsync(request.Title, request.Entry?.Id, request.Protocol, request.Host, request.Port, request.BehindJumpHost);
        return true;
    }
}

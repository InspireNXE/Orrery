using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Protocols.Telnet;

namespace Orrery.App.Protocols;

sealed class TelnetProtocol() : ProtocolDescriptor(Protocol.Telnet)
{
    public override IBrush Background { get; } = Brush("#2E2445");

    public override IBrush Foreground { get; } = Brush("#C9B0FF");

    public override string Icon => Icons.Terminal;

    /// <summary>Telnet has no login of its own: the server asks in the terminal.</summary>
    public override Task StartAsync(ISessionHost host, StartRequest request)
    {
        host.Open(request.Origin, () =>
        {
            var session = TelnetSession.Start(request.Target, 80, 24, host.Tunnel(request.Route.Jumps));
            return (host.NewTerminalView(session), session);
        });
        return Task.CompletedTask;
    }
}

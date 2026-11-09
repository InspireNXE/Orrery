using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Protocols.Vnc;

namespace Orrery.App.Protocols;

sealed class VncProtocol() : ProtocolDescriptor(Protocol.Vnc)
{
    public override IBrush Background { get; } = Brush("#3A2A17");

    public override IBrush Foreground { get; } = Brush("#F3B672");

    public override string Icon => Icons.Monitor;

    public override PropertiesSection CreateSection(PropertiesContext context) => new VncSection(context);

    /// <summary>VNC servers set their own desktop size; the view shrinks it to fit and zooms it like RDP.</summary>
    public override Task StartAsync(ISessionHost host, StartRequest request)
    {
        var (origin, target, options) = (request.Origin, request.Target, request.Options);
        host.Open(origin, () =>
        {
            var session = VncSession.Start(target, request.Login.Password!, options.ShareClipboard, route: host.Tunnel(request.Route.Jumps), quality: options.Quality);
            var view = host.NewDesktopView(origin, session, options.Enlarge ?? host.EnlargesByDefault, followsViewSize: true);
            view.ViewOnly = options.ViewOnly;
            view.Scaling = options.Scaling;
            return (view, session);
        });
        host.LoginsChanged();
        return Task.CompletedTask;
    }
}

using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;
using Orrery.Protocols.Rdp;

namespace Orrery.App.Protocols;

sealed class RdpProtocol() : ProtocolDescriptor(Protocol.Rdp)
{
    public override IBrush Background { get; } = Brush("#1D2E45");

    public override IBrush Foreground { get; } = Brush("#8CC2FF");

    public override string Icon => Icons.Monitor;

    public override IEnumerable<(string Label, string Value)> FixedSettings(ResolvedSettings saved, bool enlargeByDefault)
    {
        yield return DisplayQuality(saved);
        yield return ("Remote desktop size", string.IsNullOrEmpty(saved.DesktopSize.Value) ? "Fit the window" : saved.DesktopSize.Value!);
        yield return MatchScaling(saved, enlargeByDefault);
        foreach (var setting in SessionSettings(saved))
        {
            yield return setting;
        }

        yield return ("Remote audio", saved.RemoteAudio.Value == RemoteAudio.PlayHere ? "Play here" : "Don't play");
        yield return ("Shared drives and folders", saved.ShareAllDrives.Value ? "Every drive"
            : saved.SharedFolders.Value is { Count: > 0 } folders ? string.Join(", ", folders) : "None");
        yield return ("Console session", OnOff(saved.AdminSession.Value));
    }

    public override bool ChoosesDesktopSize => true;

    public override PropertiesSection CreateSection(PropertiesContext context) => new RdpSection(context);

    /// <summary>
    /// A fixed desktop size is kept; otherwise it fits the session area and follows it. "Match display scaling"
    /// tells a desktop that follows the view this display's scaling, as mstsc does, so it stays pixel for pixel; a
    /// fixed size is enlarged instead.
    /// </summary>
    public override Task StartAsync(ISessionHost host, StartRequest request)
    {
        var (origin, target, options) = (request.Origin, request.Target, request.Options);
        host.Open(origin, () =>
        {
            bool matchScaling = options.Enlarge ?? host.EnlargesByDefault;
            bool tellServer = matchScaling && options.DesktopSize is null;
            bool zoom = matchScaling && !tellServer;
            var (width, height) = options.DesktopSize ?? host.DesktopSizeForView(zoom);
            var problems = new List<string>();
            var rdpOptions = OptionsFor(host, origin, problems);
            var session = RdpSession.Start(target, request.Login.Password!, width, height, host.CertificateValidator, options.ShareClipboard,
                host.Tunnel(request.Route.Jumps), options.Quality, rdpOptions, tellServer ? host.DisplayScale : null);
            var view = host.NewDesktopView(origin, session, zoom, followsViewSize: options.DesktopSize is null);
            view.Scaling = options.Scaling;
            if (problems.Count > 0)
            {
                // Said once the desktop shows, where it's seen, rather than over the connecting screen.
                void OnConnected(object? sender, SessionStateChangedEventArgs e)
                {
                    if (e.State == SessionState.Connected)
                    {
                        session.StateChanged -= OnConnected;
                        Dispatcher.UIThread.Post(() => view.ShowNotice(string.Join(" ", problems)));
                    }
                }

                session.StateChanged += OnConnected;
            }

            return (view, session);
        });
        host.LoginsChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A saved connection's sound, shared folders and console session, read again at each start, with what can't be
    /// shared added to <paramref name="problems"/>; Orrery's defaults for a host typed into quick connect.
    /// </summary>
    static RdpOptions OptionsFor(ISessionHost host, SessionOrigin origin, List<string> problems)
    {
        if (origin.EntryId is not { } id || host.Find(id) is not { } node)
        {
            return new RdpOptions();
        }

        var resolved = SettingsResolver.Resolve(node);
        var drives = RdpDrives.ForThisComputer(resolved.ShareAllDrives.Value, resolved.SharedFolders.Value, problems);
        foreach (var problem in problems)
        {
            ConnectLog.LogWarning("{Name}: {Problem}", origin.Title, problem);
        }

        return new RdpOptions
        {
            PlayAudio = resolved.RemoteAudio.Value == RemoteAudio.PlayHere,
            Drives = drives,
            AdminSession = resolved.AdminSession.Value,
        };
    }

    static readonly ILogger ConnectLog = Log.For("Connect");
}

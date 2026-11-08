using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

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
}

using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Protocols.Local;

namespace Orrery.App.Protocols;

/// <summary>A shell on this computer.</summary>
sealed class LocalProtocol() : ProtocolDescriptor(Protocol.Local)
{
    public override IBrush Background { get; } = Brush("#33331A");

    public override IBrush Foreground { get; } = Brush("#E6E08A");

    public override string Icon => Icons.Terminal;

    public override HostField Host { get; } = new("Shell", "The one chosen in Settings",
        new HostChooser("Shells…", "Choose a shell on this computer", "The shells on this computer", ShellChoices));

    public override PropertiesSection CreateSection(PropertiesContext context) => new LocalSection(context);

    /// <summary>The one chosen in Settings, which is an empty host, then every shell on this computer by its id.</summary>
    static IReadOnlyList<HostChoice> ShellChoices() =>
        [new HostChoice("The one chosen in Settings", string.Empty, SeparatorAfter: true), .. LocalShells.Available().Select(s => new HostChoice(s.Name, s.Id))];
}

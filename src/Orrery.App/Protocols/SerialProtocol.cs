using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Protocols.Serial;

namespace Orrery.App.Protocols;

sealed class SerialProtocol() : ProtocolDescriptor(Protocol.Serial)
{
    public override IBrush Background { get; } = Brush("#3A2230");

    public override IBrush Foreground { get; } = Brush("#F2A7C8");

    public override string Icon => Icons.SerialPort;

    public override HostField Host { get; } = new("Serial port", OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0",
        new HostChooser("Ports…", "Choose a serial port on this computer", "The serial ports on this computer", PortChoices));

    public override PropertiesSection CreateSection(PropertiesContext context) => new SerialSection(context);

    static IReadOnlyList<HostChoice> PortChoices()
    {
        var ports = SerialSession.PortNames();
        return ports.Count == 0 ? [new HostChoice("No serial ports found", null)] : [.. ports.Select(name => new HostChoice(name, name))];
    }
}

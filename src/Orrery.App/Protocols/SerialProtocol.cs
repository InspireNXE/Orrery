using Avalonia.Media;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;
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

    public override string NoHostHelp => "Enter the serial port's name, such as COM3 or /dev/ttyUSB0, in its settings first.";

    /// <summary>A terminal on a serial port of this computer, such as a switch's console cable.</summary>
    public override Task StartAsync(ISessionHost host, StartRequest request)
    {
        var (port, line) = (request.Target.Host, request.Options.Line);
        if (request.Origin.EntryId is not null)
        {
            Log.For("Connect").LogInformation("Opening \"{Name}\": serial port {Port} at {Line}", request.Origin.Title, port, line);
        }

        host.Open(request.Origin, () =>
        {
            var session = SerialSession.Start(new ConnectionTarget(port, 0, null, null), line);
            return (host.NewTerminalView(session), session);
        });
        return Task.CompletedTask;
    }

    static IReadOnlyList<HostChoice> PortChoices()
    {
        var ports = SerialSession.PortNames();
        return ports.Count == 0 ? [new HostChoice("No serial ports found", null)] : [.. ports.Select(name => new HostChoice(name, name))];
    }
}

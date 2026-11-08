using System.Globalization;
using Avalonia.Controls;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Protocols.Serial;
using static Orrery.App.Connections.PropertyEditors;

namespace Orrery.App.Connections;

/// <summary>A serial port's line: speed, data bits, parity, stop bits and flow control.</summary>
sealed class SerialSection : PropertiesSection
{
    readonly TextBox _baudRate = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = SettingsResolver.DefaultBaudRate.ToString(CultureInfo.InvariantCulture) };
    readonly ComboBox _dataBits = Choices("DataBits", (8, "8"), (7, "7"), (6, "6"), (5, "5"));
    readonly ComboBox _parity = Choices("Parity", (SerialParity.None, "None"), (SerialParity.Even, "Even"), (SerialParity.Odd, "Odd"), (SerialParity.Mark, "Mark"), (SerialParity.Space, "Space"));
    readonly ComboBox _stopBits = Choices("StopBits", (SerialStopBits.One, "1"), (SerialStopBits.OnePointFive, "1.5"), (SerialStopBits.Two, "2"));
    readonly ComboBox _flowControl = Choices("FlowControl", (SerialFlowControl.None, "None"), (SerialFlowControl.XOnXOff, "XON/XOFF (software)"), (SerialFlowControl.RtsCts, "RTS/CTS (hardware)"));
    readonly InheritRow _baudRateRow, _dataBitsRow, _parityRow, _stopBitsRow, _flowControlRow;

    public SerialSection(PropertiesContext context) : base(context)
    {
        _baudRateRow = new InheritRow("Speed (baud)", _baudRate, () => Context.ShowInherited(r => _baudRate.Text = r.BaudRate.Value.ToString(CultureInfo.InvariantCulture)));
        _baudRateRow.Tip("Serial: bits per second, the same as the device's. Consoles mostly use 9600 or 115200.");
        _dataBitsRow = new InheritRow("Data bits", _dataBits, () => Context.ShowInherited(r => Choose(_dataBits, r.DataBits.Value)));
        _parityRow = new InheritRow("Parity", _parity, () => Context.ShowInherited(r => Choose(_parity, r.Parity.Value)));
        _stopBitsRow = new InheritRow("Stop bits", _stopBits, () => Context.ShowInherited(r => Choose(_stopBits, r.StopBits.Value)));
        _flowControlRow = new InheritRow("Flow control", _flowControl, () => Context.ShowInherited(r => Choose(_flowControl, r.FlowControl.Value)));
        _flowControlRow.Tip("Serial: how the two ends pause each other. Most consoles use none; use what the device's manual says.");
        View = Pane.RowSection("Serial line", _baudRateRow.View, _dataBitsRow.View, _parityRow.View, _stopBitsRow.View, _flowControlRow.View);
    }

    public override Control View { get; }

    public override IEnumerable<InheritRow> Rows => [_baudRateRow, _dataBitsRow, _parityRow, _stopBitsRow, _flowControlRow];

    public override IEnumerable<Control> Fields => [_baudRate, _dataBits, _parity, _stopBits, _flowControl];

    public override void Load(ConnectionNode node, InheritableSettings own, ResolvedSettings inherited)
    {
        _baudRateRow.Load(own.BaudRate is null, Describe(inherited.BaudRate));
        _baudRate.Text = (own.BaudRate ?? inherited.BaudRate.Value).ToString(CultureInfo.InvariantCulture);
        _dataBitsRow.Load(own.DataBits is null, Describe(inherited.DataBits));
        Choose(_dataBits, own.DataBits ?? inherited.DataBits.Value);
        _parityRow.Load(own.Parity is null, Describe(inherited.Parity));
        Choose(_parity, own.Parity ?? inherited.Parity.Value);
        _stopBitsRow.Load(own.StopBits is null, Describe(inherited.StopBits));
        Choose(_stopBits, own.StopBits ?? inherited.StopBits.Value);
        _flowControlRow.Load(own.FlowControl is null, Describe(inherited.FlowControl));
        Choose(_flowControl, own.FlowControl ?? inherited.FlowControl.Value);
    }

    public override string? Save(InheritableSettings settings)
    {
        int? baudRate = null;
        if (!_baudRateRow.Inheriting)
        {
            if (!int.TryParse(_baudRate.Text?.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var baud)
                || (SerialLine.Default with { BaudRate = baud }).Problem() is not null)
            {
                return "The speed must be a number of bits per second from 50 to 4,000,000, such as 9600 or 115200.";
            }

            baudRate = baud;
        }

        settings.BaudRate = baudRate;
        settings.DataBits = _dataBitsRow.Inheriting ? null : Chosen<int>(_dataBits);
        settings.Parity = _parityRow.Inheriting ? null : Chosen<SerialParity>(_parity);
        settings.StopBits = _stopBitsRow.Inheriting ? null : Chosen<SerialStopBits>(_stopBits);
        settings.FlowControl = _flowControlRow.Inheriting ? null : Chosen<SerialFlowControl>(_flowControl);
        return null;
    }
}

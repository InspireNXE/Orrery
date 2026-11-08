using Avalonia.Controls;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using static Orrery.App.Connections.PropertyEditors;

namespace Orrery.App.Connections;

/// <summary>VNC: watching only.</summary>
sealed class VncSection : PropertiesSection
{
    readonly ToggleSwitch _viewOnly = Switch("ViewOnly");
    readonly InheritRow _viewOnlyRow;

    public VncSection(PropertiesContext context) : base(context)
    {
        _viewOnlyRow = new InheritRow("View only", _viewOnly, () => Context.ShowInherited(r => _viewOnly.IsChecked = r.ViewOnly.Value));
        _viewOnlyRow.Tip("VNC: watch the remote desktop without sending keys, clicks or the clipboard. The session's More menu turns it off.");
        View = Pane.RowSection("VNC", _viewOnlyRow.View);
    }

    public override Control View { get; }

    public override IEnumerable<InheritRow> Rows => [_viewOnlyRow];

    public override IEnumerable<Control> Fields => [_viewOnly];

    public override IEnumerable<(InheritRow Row, string Key)> LiveRows => [(_viewOnlyRow, SessionSettingsView.ViewOnlyKey)];

    public override void Load(ConnectionNode node, InheritableSettings own, ResolvedSettings inherited)
    {
        _viewOnlyRow.Load(own.ViewOnly is null, Describe(inherited.ViewOnly));
        _viewOnly.IsChecked = own.ViewOnly ?? inherited.ViewOnly.Value;
    }

    public override string? Save(InheritableSettings settings)
    {
        settings.ViewOnly = _viewOnlyRow.Inheriting ? null : _viewOnly.IsChecked == true;
        return null;
    }
}

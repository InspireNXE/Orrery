using Avalonia.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>What a protocol's section in the properties pane can ask of the pane.</summary>
sealed class PropertiesContext(Action<Action<ResolvedSettings>> showInherited, Func<TopLevel?> topLevel, Action<string> fail)
{
    /// <summary>Applies <paramref name="apply"/> to what the node shown would inherit, for a row's "use that instead".</summary>
    public void ShowInherited(Action<ResolvedSettings> apply) => showInherited(apply);

    /// <summary>The window, for file and folder pickers.</summary>
    public TopLevel? TopLevel => topLevel();

    /// <summary>Shows a problem under the fields, as a failed save does.</summary>
    public void Fail(string message) => fail(message);
}

/// <summary>
/// One protocol's own settings in the properties pane, such as RDP's drives or a serial line's speed. The pane shows it
/// for that protocol's connections, and for folders and the defaults, whose settings reach every protocol's.
/// </summary>
abstract class PropertiesSection(PropertiesContext context)
{
    protected PropertiesContext Context { get; } = context;

    public abstract Control View { get; }

    /// <summary>False for one that's only about a connection itself, so a folder doesn't show it.</summary>
    public virtual bool ForFolders => true;

    /// <summary>Its inheritable rows, for the pane to count and watch.</summary>
    public virtual IEnumerable<InheritRow> Rows => [];

    /// <summary>Every editor it saves from, hidden ones too, for telling whether anything has changed.</summary>
    public abstract IEnumerable<Control> Fields { get; }

    /// <summary>Its rows an open session takes up at once from its This session tab, with that tab's keys for them.</summary>
    public virtual IEnumerable<(InheritRow Row, string Key)> LiveRows => [];

    /// <summary>Shows <paramref name="node"/>'s own settings, and what it inherits where it sets none.</summary>
    public abstract void Load(ConnectionNode node, InheritableSettings own, ResolvedSettings inherited);

    /// <summary>
    /// Checks what's typed and puts the section's settings in <paramref name="settings"/>, the node's new ones. The
    /// problem to show when something is wrong, null when it's all right.
    /// </summary>
    public virtual string? Save(InheritableSettings settings) => null;

    /// <summary>Puts what it edits on the connection itself, not inherited, once everything has been checked.</summary>
    public virtual void SaveEntry(ConnectionEntry entry)
    {
    }
}

/// <summary>A choice from a protocol's list of hosts on this computer, such as a serial port.</summary>
/// <param name="Value">What the host box gets; null for a line that isn't a choice ("No serial ports found").</param>
/// <param name="SeparatorAfter">A line under it, between it and the rest.</param>
sealed record HostChoice(string Label, string? Value, bool SeparatorAfter = false);

/// <summary>A button beside the host box that lists what can go in it, such as this computer's serial ports.</summary>
sealed record HostChooser(string Button, string Name, string Tip, Func<IReadOnlyList<HostChoice>> Choices);

/// <summary>What a protocol calls its host, and what the box suggests when it's empty.</summary>
sealed record HostField(string Label, string? Placeholder = null, HostChooser? Chooser = null)
{
    public static readonly HostField Network = new("Host");
}

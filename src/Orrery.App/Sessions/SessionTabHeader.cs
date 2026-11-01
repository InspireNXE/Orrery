using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Orrery.App.Sessions;

/// <summary>
/// A session's header in the tab strip. It looks like the other tab buttons, but a screen reader hears a tab with
/// the session's name, its state and whether it is the current one.
/// </summary>
sealed class SessionTabHeader : Button
{
    string _label = string.Empty;
    string _status = string.Empty;
    bool _isCurrent;

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>What a screen reader calls the tab, such as "web01, SSH".</summary>
    public string Label
    {
        get => _label;
        set => Change(ref _label, value, AutomationElementIdentifiers.NameProperty);
    }

    /// <summary>The session's state, such as "Connected".</summary>
    public string Status
    {
        get => _status;
        set => Change(ref _status, value, AutomationElementIdentifiers.ItemStatusProperty);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Change(ref _isCurrent, value, SelectionItemPatternIdentifiers.IsSelectedProperty);
    }

    void Change<T>(ref T field, T value, AutomationProperty property)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        var old = field;
        field = value;
        (ControlAutomationPeer.FromElement(this) as Peer)?.Changed(property, old, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(SessionTabHeader owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
    {
        SessionTabHeader Header => (SessionTabHeader)Owner;

        public void Changed(AutomationProperty property, object? old, object? value) =>
            RaisePropertyChangedEvent(property, old, value);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

        protected override string? GetNameCore()
        {
            var name = AutomationProperties.GetName(Owner);
            return string.IsNullOrEmpty(name) ? Header.Label : name;
        }

        protected override string? GetItemStatusCore() => Header.Status;

        public bool IsSelected => Header.IsCurrent;

        public ISelectionProvider? SelectionContainer => null;

        public void Select() => Invoke();

        public void AddToSelection() => Invoke();

        public void RemoveFromSelection()
        {
        }
    }
}

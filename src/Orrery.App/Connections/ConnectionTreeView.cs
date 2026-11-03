using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Orrery.App.Connections;

/// <summary>
/// The connection tree. Avalonia's tree rows tell a screen reader neither their name nor whether they're selected,
/// so its rows are <see cref="ConnectionTreeItem"/>s, which do.
/// </summary>
sealed class ConnectionTreeView : TreeView
{
    protected override Type StyleKeyOverride => typeof(TreeView);

    // Rows below the top level come from here too: a TreeViewItem asks its tree for containers.
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new ConnectionTreeItem();

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<ConnectionTreeItem>(item, out recycleKey);
}

/// <summary>A row in <see cref="ConnectionTreeView"/>, read as its connection or folder.</summary>
sealed class ConnectionTreeItem : TreeViewItem
{
    protected override Type StyleKeyOverride => typeof(TreeViewItem);

    protected override AutomationPeer OnCreateAutomationPeer() => new ConnectionTreeItemPeer(this);
}

/// <summary>
/// A tree row as a screen reader sees it: named after its node, with what it is (protocol and host, or how many
/// connections a folder holds) as its description, the open session's state as its status, and expand, collapse and
/// select. Avalonia's own peer reports whether the row is open; this one adds the rest.
/// </summary>
sealed class ConnectionTreeItemPeer : TreeViewItemAutomationPeer, IExpandCollapseProvider, ISelectionItemProvider
{
    NodeViewModel? _row;

    public ConnectionTreeItemPeer(ConnectionTreeItem owner)
        : base(owner)
    {
        owner.PropertyChanged += (_, e) =>
        {
            if (e.Property == TreeViewItem.IsSelectedProperty)
            {
                RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, e.OldValue, e.NewValue);
            }
            else if (e.Property == StyledElement.DataContextProperty)
            {
                Watch(e.NewValue as NodeViewModel);
                RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, null, GetName());
            }
        };
        Watch(owner.DataContext as NodeViewModel);
    }

    NodeViewModel? Row => Owner.DataContext as NodeViewModel;

    /// <summary>How deep the row is: 0 at the top. Avalonia doesn't pass this on to UI Automation; readers work it out from the nesting.</summary>
    public int Level => Owner.Level;

    void Watch(NodeViewModel? row)
    {
        if (_row is not null)
        {
            _row.PropertyChanged -= OnRowChanged;
        }

        _row = row;
        if (row is not null)
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeViewModel.SpokenStatus))
        {
            RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, null, GetItemStatus());
        }
    }

    protected override string? GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : Row?.Name;

    protected override string? GetHelpTextCore() => Row?.Description ?? base.GetHelpTextCore();

    protected override string? GetItemStatusCore() => Row?.SpokenStatus ?? base.GetItemStatusCore();

    // ---- Expand and collapse ----

    // Unlike Avalonia's, these don't throw on a connection, which has nothing to open.
    public new void Expand()
    {
        EnsureEnabled();
        Owner.IsExpanded = true;
    }

    public new void Collapse()
    {
        EnsureEnabled();
        Owner.IsExpanded = false;
    }

    // ---- Selection ----

    TreeView? Tree => Owner.FindLogicalAncestorOfType<TreeView>();

    public new void Select()
    {
        EnsureEnabled();
        if (Tree is { } tree)
        {
            tree.SelectedItem = Owner.DataContext;
        }
    }

    public void AddToSelection()
    {
        EnsureEnabled();
        if (Tree is { SelectionMode: SelectionMode.Multiple } tree && Owner.DataContext is { } row && !tree.SelectedItems.Contains(row))
        {
            tree.SelectedItems.Add(row);
        }
        else
        {
            Select();
        }
    }

    public void RemoveFromSelection()
    {
        EnsureEnabled();
        Tree?.SelectedItems.Remove(Owner.DataContext);
    }
}

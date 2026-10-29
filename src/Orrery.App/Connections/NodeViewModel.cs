using System.Collections.ObjectModel;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>
/// One row in the connection tree. Rebuilt from the model whenever the tree changes; the model stays
/// the source of truth. While filtering, rows are force-expanded and expansion isn't saved.
/// </summary>
public sealed class NodeViewModel
{
    readonly Action? _expansionChanged;
    readonly bool _persistExpansion;
    bool _isExpanded;

    public NodeViewModel(ConnectionNode node, IEnumerable<NodeViewModel> children, bool forceExpanded, Action? expansionChanged)
    {
        Node = node;
        Children = new ObservableCollection<NodeViewModel>(children);
        _expansionChanged = expansionChanged;
        _persistExpansion = !forceExpanded;
        _isExpanded = forceExpanded || (node as ConnectionFolder)?.Expanded == true;
    }

    public ConnectionNode Node { get; }
    public ObservableCollection<NodeViewModel> Children { get; }

    public string Name => string.IsNullOrWhiteSpace(Node.Name) ? "(unnamed)" : Node.Name;
    public bool IsFolder => Node is ConnectionFolder;
    public bool IsConnection => Node is ConnectionEntry;
    public string Badge => IsConnection ? ProtocolInfo.Label(SettingsResolver.Resolve(Node).Protocol.Value) : string.Empty;
    public string Detail => Node is ConnectionEntry entry ? entry.Host : $"{((ConnectionFolder)Node).Children.Count}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            if (_persistExpansion && Node is ConnectionFolder folder)
            {
                folder.Expanded = value;
                _expansionChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Builds rows for the root's children. With a filter, keeps connections whose name, host or folder
    /// path matches, plus the folders leading to them.
    /// </summary>
    public static List<NodeViewModel> Build(ConnectionFolder root, string? filter, Action expansionChanged)
    {
        var query = filter?.Trim();
        bool filtering = !string.IsNullOrEmpty(query);
        return root.Children.Select(n => BuildNode(n, query, filtering, expansionChanged)).OfType<NodeViewModel>().ToList();
    }

    static NodeViewModel? BuildNode(ConnectionNode node, string? query, bool filtering, Action expansionChanged)
    {
        if (node is ConnectionFolder folder)
        {
            var children = folder.Children.Select(c => BuildNode(c, query, filtering, expansionChanged)).OfType<NodeViewModel>().ToList();
            bool folderMatches = filtering && Matches(folder.Name, query!);
            if (filtering && children.Count == 0 && !folderMatches)
            {
                return null;
            }

            // A matching folder shows all its contents.
            if (folderMatches && children.Count == 0)
            {
                children = folder.Children.Select(c => BuildNode(c, null, false, expansionChanged)).OfType<NodeViewModel>().ToList();
            }

            return new NodeViewModel(folder, children, filtering, expansionChanged);
        }

        var entry = (ConnectionEntry)node;
        if (filtering && !Matches(entry.Name, query!) && !Matches(entry.Host, query!))
        {
            return null;
        }

        return new NodeViewModel(entry, [], filtering, expansionChanged);
    }

    static bool Matches(string text, string query) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}

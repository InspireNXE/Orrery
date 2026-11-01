using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;

namespace Orrery.App.Connections;

/// <summary>
/// One row in the connection tree. Rebuilt from the model whenever the tree changes; the model stays
/// the source of truth. While filtering, rows are force-expanded and expansion isn't saved.
/// </summary>
public sealed class NodeViewModel : INotifyPropertyChanged
{
    static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#5FD38D")).ToImmutable();
    static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.Parse("#E0B45A")).ToImmutable();
    static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#5B6372")).ToImmutable();

    readonly Action? _expansionChanged;
    readonly bool _persistExpansion;
    bool _isExpanded;
    SessionState? _status;

    public NodeViewModel(ConnectionNode node, IEnumerable<NodeViewModel> children, bool forceExpanded, Action? expansionChanged)
    {
        Node = node;
        Children = new ObservableCollection<NodeViewModel>(children);
        _expansionChanged = expansionChanged;
        _persistExpansion = !forceExpanded;
        _isExpanded = forceExpanded || node switch
        {
            ConnectionFolder folder => folder.Expanded,
            ConnectionEntry entry => !entry.GuestsCollapsed,
            _ => false,
        };
    }

    public ConnectionNode Node { get; }
    public ObservableCollection<NodeViewModel> Children { get; }

    public string Name => string.IsNullOrWhiteSpace(Node.Name) ? "(unnamed)" : Node.Name;
    public bool IsFolder => Node is ConnectionFolder;
    public bool IsConnection => Node is ConnectionEntry;
    public bool IsHost => Node is ConnectionEntry { IsHost: true };

    /// <summary>How many guests a host has, shown beside its badge.</summary>
    public string GuestCount => Node is ConnectionEntry { IsHost: true } host ? host.Guests.Count.ToString() : string.Empty;
    Protocol Protocol => SettingsResolver.Resolve(Node).Protocol.Value;
    public string Badge => IsConnection ? ProtocolInfo.Label(Protocol) : string.Empty;
    public IBrush BadgeBackground => ProtocolStyle.Background(Protocol);
    public IBrush BadgeForeground => ProtocolStyle.Foreground(Protocol);
    public string Detail => Node is ConnectionEntry entry ? entry.Host : $"{((ConnectionFolder)Node).Children.Count}";

    /// <summary>How many connections a folder holds, at any depth.</summary>
    public string Count => Node is ConnectionFolder folder ? folder.Descendants().OfType<ConnectionEntry>().Count().ToString() : string.Empty;

    /// <summary>The state of this connection's open session, or null when none is open.</summary>
    public SessionState? Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusFill)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusStroke)));
        }
    }

    // A filled dot for an open session, a hollow ring otherwise.
    public IBrush? StatusFill => _status switch
    {
        SessionState.Connected => ConnectedBrush,
        SessionState.Connecting => ConnectingBrush,
        _ => null,
    };

    public IBrush? StatusStroke => StatusFill is null ? OfflineBrush : null;

    /// <summary>What a screen reader says after the name: "SSH, web01.example, 2 guests" or "Folder, 3 connections".</summary>
    public string Description => Node switch
    {
        ConnectionFolder => $"Folder, {Plural(int.Parse(Count), "connection")}",
        ConnectionEntry { IsHost: true } host => $"{Badge}, {Where(host)}, {Plural(host.Guests.Count, "guest")}",
        ConnectionEntry { GuestOf: { } owner } guest => $"{Badge}, {Where(guest)}, guest of {owner.Name}",
        ConnectionEntry entry => $"{Badge}, {Where(entry)}",
        _ => string.Empty,
    };

    /// <summary>The open session's state in words, for a screen reader; empty when none is open.</summary>
    public string StatusText => _status switch
    {
        SessionState.Connected => "Connected",
        SessionState.Connecting => "Connecting",
        _ => string.Empty,
    };

    static string Where(ConnectionEntry entry) => string.IsNullOrEmpty(entry.Host) ? "no host" : entry.Host;

    static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public event PropertyChangedEventHandler? PropertyChanged;

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
            else if (_persistExpansion && Node is ConnectionEntry { IsHost: true } host)
            {
                host.GuestsCollapsed = !value;
                _expansionChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Builds rows for the root's children, with each host's guests under it. With a filter, keeps
    /// connections whose name or host matches, plus the folders and hosts leading to them; a matching
    /// folder or host shows everything in it.
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
        bool entryMatches = !filtering || Matches(entry.Name, query!) || Matches(entry.Host, query!);
        var guests = entry.Guests
            .Where(g => entryMatches || Matches(g.Name, query!) || Matches(g.Host, query!))
            .Select(g => new NodeViewModel(g, [], filtering, expansionChanged))
            .ToList();
        if (!entryMatches && guests.Count == 0)
        {
            return null;
        }

        return new NodeViewModel(entry, guests, filtering, expansionChanged);
    }

    static bool Matches(string text, string query) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}

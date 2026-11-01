using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Orrery.Core.Connections;
using Orrery.Core.Network;
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
    HostState? _reachability;
    string? _reachabilityTip;

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

    /// <summary>
    /// Whether the host answered the last check: a green dot for up, red for down, a grey ring for unknown.
    /// Null when checks are off (no dot), or for a folder.
    /// </summary>
    public HostState? Reachability
    {
        get => _reachability;
        private set
        {
            if (_reachability == value)
            {
                return;
            }

            _reachability = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Reachability)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReachabilityShown)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReachabilityUp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReachabilityDown)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReachabilityUnknown)));
        }
    }

    public bool ReachabilityShown => _reachability is not null;

    // Each state has its own shape as well as colour: a filled dot, a cross, a dashed ring.
    public bool ReachabilityUp => _reachability == HostState.Up;
    public bool ReachabilityDown => _reachability == HostState.Down;
    public bool ReachabilityUnknown => _reachability == HostState.Unknown;

    /// <summary>What the dot means, and when it was checked.</summary>
    public string? ReachabilityTip
    {
        get => _reachabilityTip;
        private set
        {
            if (_reachabilityTip == value)
            {
                return;
            }

            _reachabilityTip = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReachabilityTip)));
        }
    }

    /// <summary>Sets the dot and its tooltip; <paramref name="state"/> null hides the dot.</summary>
    public void SetReachability(HostState? state, string? tip)
    {
        Reachability = state;
        ReachabilityTip = state is null ? null : tip;
    }

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

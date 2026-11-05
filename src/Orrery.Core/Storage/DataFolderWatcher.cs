namespace Orrery.Core.Storage;

/// <summary>
/// Says when one of the data folder's shared files changes on disk: a sync app bringing in another computer's save, or
/// this computer's own (the listener compares contents, so those cost a read). Changes come together after
/// <see cref="Settle"/>, as a sync app often writes a file in steps. File events can be missed on network and synced
/// drives, so the files are also checked every <see cref="PollEvery"/> by size and time.
/// </summary>
public sealed class DataFolderWatcher : IDisposable
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(20);

    readonly string _dir;
    readonly HashSet<string> _names;
    readonly FileSystemWatcher? _watcher;
    readonly Timer _settle;
    readonly Timer _poll;
    readonly object _lock = new();
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (long Length, DateTime Written)> _seen = new(StringComparer.OrdinalIgnoreCase);
    bool _disposed;

    /// <summary>Raised on a background thread with the names of the files that changed.</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    public DataFolderWatcher(string dataDir, IEnumerable<string> fileNames)
    {
        _dir = dataDir;
        _names = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _names)
        {
            _seen[name] = Stamp(name);
        }

        _settle = new Timer(_ => Raise());
        _poll = new Timer(_ => Poll(), null, PollEvery, PollEvery);
        try
        {
            _watcher = new FileSystemWatcher(dataDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, e) => Saw(e.Name);
            _watcher.Created += (_, e) => Saw(e.Name);
            _watcher.Deleted += (_, e) => Saw(e.Name);
            _watcher.Renamed += (_, e) =>
            {
                Saw(e.Name);
                Saw(e.OldName);
            };
            _watcher.Error += (_, _) => Poll();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // The poll still runs.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    (long, DateTime) Stamp(string name)
    {
        try
        {
            var info = new FileInfo(Path.Combine(_dir, name));
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (-1, default);
        }
    }

    void Saw(string? name)
    {
        if (name is null || !_names.Contains(name))
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(name);
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    void Poll()
    {
        foreach (var name in _names)
        {
            var stamp = Stamp(name);
            bool changed;
            lock (_lock)
            {
                changed = _seen[name] != stamp;
            }

            if (changed)
            {
                Saw(name);
            }
        }
    }

    void Raise()
    {
        List<string> names;
        lock (_lock)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            names = [.. _pending];
            _pending.Clear();
            foreach (var name in names)
            {
                _seen[name] = Stamp(name);
            }
        }

        Changed?.Invoke(names);
    }

    /// <summary>Raises <see cref="Changed"/> now for what's waiting. For tests.</summary>
    internal void RaiseNow() => Raise();

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _watcher?.Dispose();
        _settle.Dispose();
        _poll.Dispose();
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orrery.Core.Storage;

/// <summary>Another computer with Orrery open on the same data folder, as a sync app copies it there.</summary>
public sealed record OtherComputer(string Tag, string Name, DateTimeOffset LastSeen);

/// <summary>
/// Tells computers sharing a data folder through a sync app (Synology Drive, OneDrive, Dropbox...) about each other.
/// <see cref="DataFolderLock"/> only stops a second Orrery on the same computer; across computers each copy of Orrery
/// would save over the other's changes, or leave the sync app's conflict copies. So each computer keeps its own small
/// file in the folder, <c>open-on-&lt;tag&gt;.json</c>, saying when Orrery was last open there, rewritten every
/// <see cref="Every"/> and deleted on closing. One per computer, so two never write the same file.
/// </summary>
public sealed class DataFolderPresence : IDisposable
{
    public const string FilePrefix = "open-on-";

    /// <summary>How often this computer's file is rewritten, and others' are read.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>A file not rewritten for this long is from an Orrery that stopped without closing, or a sync that stalled.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    readonly string _dataDir;
    readonly string _path;
    readonly Func<DateTimeOffset> _clock;
    readonly HashSet<string> _announced = [];
    readonly object _lock = new();
    Timer? _timer;
    bool _disposed;

    public DataFolderPresence(string dataDir, string computerName, Func<DateTimeOffset>? clock = null)
    {
        _dataDir = dataDir;
        ComputerName = computerName;
        Tag = ComputerTag(computerName);
        _path = Path.Combine(dataDir, FilePrefix + Tag + ".json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>This computer's name, as other computers' warnings show it.</summary>
    public string ComputerName { get; }

    /// <summary>A short tag for this computer: in file names and the log, which don't hold its name.</summary>
    public string Tag { get; }

    /// <summary>
    /// Raised on a timer thread when another computer is seen with the folder open that wasn't when this one started,
    /// or since it was last raised for it.
    /// </summary>
    public event Action<OtherComputer>? OtherOpened;

    /// <summary>Six hex digits from the computer's name: the same on every start, different between computers.</summary>
    public static string ComputerTag(string computerName) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("orrery computer:" + computerName.ToUpperInvariant())))[..6];

    /// <summary>Marks this computer as having the folder open, notes who else has, and keeps it up every <see cref="Every"/>.</summary>
    public IReadOnlyList<OtherComputer> Start()
    {
        Mark();
        var others = Others();
        lock (_lock)
        {
            foreach (var other in others)
            {
                _announced.Add(other.Tag);
            }

            _timer = new Timer(_ => Tick(), null, Every, Every);
        }

        return others;
    }

    /// <summary>Rewrites this computer's file with the time now.</summary>
    public void Mark()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new Entry(ComputerName, _clock())));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A sync app holding the file: next time.
            }
        }
    }

    /// <summary>The other computers whose file says Orrery was open there within <see cref="StaleAfter"/>.</summary>
    public IReadOnlyList<OtherComputer> Others()
    {
        var now = _clock();
        var found = new List<OtherComputer>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dataDir, FilePrefix + "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var tag = name[FilePrefix.Length..];
                if (tag == Tag || tag.Length != 6)
                {
                    continue;
                }

                if (Read(file) is { } entry && now - entry.Seen < StaleAfter && entry.Seen - now < StaleAfter)
                {
                    found.Add(new OtherComputer(tag, entry.Computer, entry.Seen));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    void Tick()
    {
        Mark();
        var others = Others();
        var fresh = new List<OtherComputer>();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var other in others)
            {
                if (_announced.Add(other.Tag))
                {
                    fresh.Add(other);
                }
            }

            // One that closed can be said again if it opens again.
            _announced.IntersectWith(others.Select(o => o.Tag));
        }

        foreach (var other in fresh)
        {
            OtherOpened?.Invoke(other);
        }
    }

    /// <summary>Runs one round of the timer's work now. For tests.</summary>
    internal void TickNow() => Tick();

    static Entry? Read(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 4096)
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
            return entry is { Computer.Length: > 0 and <= 255 } ? entry : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            try
            {
                File.Delete(_path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    sealed record Entry(string Computer, DateTimeOffset Seen);
}

/// <summary>
/// Copies a sync app made of the data folder's files when two computers changed one before it could copy either
/// change: Dropbox's "(… conflicted copy …)", Synology Drive's and Nextcloud's "Conflict", Syncthing's
/// ".sync-conflict-", and OneDrive's name with another computer's added. Orrery only reads the files under their own
/// names, so what's in a copy is lost unless someone looks.
/// </summary>
public static class SyncConflicts
{
    /// <summary>The conflict copies at the top of <paramref name="dataDir"/> of the files Orrery keeps there.</summary>
    /// <param name="computerNames">Other computers known to use the folder, for OneDrive's copies.</param>
    public static IReadOnlyList<string> Find(string dataDir, IEnumerable<string>? computerNames = null)
    {
        var stems = DataFolderFiles.Files.Where(f => !f.Contains('*')).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var names = (computerNames ?? []).Where(n => n.Length > 0).ToList();
        var found = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dataDir))
            {
                var name = Path.GetFileName(file);
                if (DataFolderFiles.Files.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith(DataFolderPresence.FilePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var stem = stems.FirstOrDefault(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase));
                if (stem is null)
                {
                    continue;
                }

                var rest = name[stem.Length..];
                if (rest.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                    || names.Any(n => rest.StartsWith("-" + n, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(name);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }
}

using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.App.Credentials;
using Orrery.App.Sessions;
using Orrery.Core.Credentials;
using Orrery.Core.Diagnostics;
using Orrery.Core.Recording;
using Orrery.Core.Settings;
using Orrery.Core.Storage;

namespace Orrery.App.Recording;

/// <summary>
/// Starts and stops tabs' recordings (docs/design-session-recording.md): finds the recordings key, names the file,
/// connects the recorder to the tab's view, and keeps the recordings folder within its limits.
/// </summary>
sealed class RecordingService(DataLocation location, AppSettingsStore settingsStore, ICredentialStore credentials)
{
    static readonly ILogger Logger = Log.For("Recording");

    readonly HashSet<SessionRecorder> _active = [];
    readonly HashSet<string> _playing = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();

    public const string FolderName = "recordings";

    public string Folder => location.FileIn(FolderName);

    public ICredentialStore Credentials => credentials;

    /// <summary>The settings now, read fresh, since Settings may have changed them.</summary>
    public AppSettings Settings => settingsStore.Load();

    /// <summary>Files being written or played, which retention leaves alone.</summary>
    public IReadOnlySet<string> InUse
    {
        get
        {
            lock (_lock)
            {
                return _active.Select(r => r.Path).Concat(_playing).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Raised on the UI thread when a recording starts or stops, so a list of them can show it.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether <paramref name="path"/> is being recorded now.</summary>
    public bool IsRecording(string path)
    {
        lock (_lock)
        {
            return _active.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Starts recording <paramref name="tab"/>. Asks for the vault only the first time anything is recorded, to keep
    /// the private key in it; after that the public key in settings is enough.
    /// </summary>
    /// <returns>Null when it couldn't start, with why in <paramref name="problem"/>.</returns>
    public async Task<(SessionRecorder? Recorder, string? Problem)> StartAsync(SessionTab tab, Window owner)
    {
        if (tab.View is not (TerminalView or RemoteFrameView))
        {
            return (null, "Only terminals and remote desktops can be recorded.");
        }

        byte[] publicKey;
        try
        {
            if (await PublicKeyAsync(owner) is not { } key)
            {
                return (null, "Recording needs the vault unlocked once, to keep the key that plays recordings.");
            }

            publicKey = key;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException)
        {
            return (null, $"Recording couldn't start: {e.Message}");
        }

        var settings = Settings;
        var started = DateTimeOffset.Now;
        var info = new RecordingInfo(tab.Origin.Title, tab.Origin.Label, started);
        SessionRecorder recorder;
        try
        {
            Directory.CreateDirectory(Folder);
            var path = NewPath(tab.Origin.Title, started, settings.PutsNamesInRecordingFiles);
            if (tab.View is TerminalView terminal)
            {
                var (columns, rows) = terminal.GridSize;
                recorder = new TerminalRecorder(RecordingWriter.Create(path, RecordingKind.Terminal, publicKey), info, columns, rows, settings.RecordingFileBytes);
            }
            else
            {
                recorder = new ScreenRecorder(RecordingWriter.Create(path, RecordingKind.Screen, publicKey), info, settings.RecordingFramesPerSecond, settings.RecordingFileBytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"Recording couldn't start: {e.Message}");
        }

        lock (_lock)
        {
            _active.Add(recorder);
        }

        recorder.Stopped += (_, _) =>
        {
            lock (_lock)
            {
                _active.Remove(recorder);
            }

            Dispatcher.UIThread.Post(() =>
            {
                ApplyRetention();
                Changed?.Invoke(this, EventArgs.Empty);
            });
        };
        Bind(recorder, tab);
        Changed?.Invoke(this, EventArgs.Empty);
        Logger.LogInformation("Recording started: {File}", Path.GetFileName(recorder.Path));
        return (recorder, null);
    }

    /// <summary>Connects a recorder to the tab's session and view: when it starts, and for the new session after a reconnect.</summary>
    public static void Bind(SessionRecorder recorder, SessionTab tab)
    {
        recorder.Attach(tab.Session);
        switch (recorder, tab.View)
        {
            case (TerminalRecorder terminal, TerminalView view):
                void Resized(object? sender, EventArgs e)
                {
                    if (recorder.IsRecording)
                    {
                        var (columns, rows) = view.GridSize;
                        terminal.Resized(columns, rows);
                    }
                    else
                    {
                        view.GridResized -= Resized;
                    }
                }

                view.GridResized += Resized;
                var (c, r) = view.GridSize;
                terminal.Resized(c, r);
                break;
            case (ScreenRecorder screen, RemoteFrameView view):
                screen.Pointer = () => view.LastRemotePointer;
                break;
        }
    }

    async Task<byte[]?> PublicKeyAsync(Window owner)
    {
        var settings = Settings;
        if (settings.RecordingPublicKey is { } saved)
        {
            var key = new byte[RecordingKeys.KeySize];
            if (Convert.TryFromBase64String(saved, key, out int written) && written == RecordingKeys.KeySize)
            {
                return key;
            }
        }

        // First recording, or the setting was lost: the key comes from the store, made there if it isn't yet.
        if (credentials.IsLocked && !await VaultUnlockWindow.EnsureUnlockedAsync(credentials, owner))
        {
            return null;
        }

        var publicKey = await RecordingKeys.EnsureAsync(credentials);
        settingsStore.Save(Settings with { RecordingPublicKey = Convert.ToBase64String(publicKey) });
        return publicKey;
    }

    /// <summary>The private key, unlocking the store first if need be; null when it's locked and stays so, or there's none.</summary>
    public async Task<byte[]?> PrivateKeyAsync(Window owner)
    {
        if (credentials.IsLocked && !await VaultUnlockWindow.EnsureUnlockedAsync(credentials, owner))
        {
            return null;
        }

        return await RecordingKeys.PrivateKeyAsync(credentials);
    }

    string NewPath(string title, DateTimeOffset started, bool withName)
    {
        var name = $"{started:yyyy-MM-dd HH.mm.ss}";
        if (withName && SafeName(title) is { Length: > 0 } safe)
        {
            name += " " + safe;
        }

        while (true)
        {
            var path = Path.Combine(Folder, $"{name} {Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2))}{RecordingFile.Extension}");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    /// <summary>A name with what a file name can't hold on any system replaced, kept short.</summary>
    internal static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var safe = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length > 60 ? safe[..60].TrimEnd() : safe;
    }

    /// <summary>Marks a file as being played, so retention leaves it, until the returned handle is disposed.</summary>
    public IDisposable Playing(string path)
    {
        lock (_lock)
        {
            _playing.Add(path);
        }

        return new Release(() =>
        {
            lock (_lock)
            {
                _playing.Remove(path);
            }
        });
    }

    public void ApplyRetention()
    {
        var settings = Settings;
        try
        {
            RecordingRetention.Apply(Folder, settings.RecordingKeepFor, settings.RecordingTotalBytes, DateTime.UtcNow, InUse);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Couldn't tidy the recordings folder: {Message}", e.Message);
        }
    }

    /// <summary>Stops every recording and waits for the files to close, as Orrery quits.</summary>
    public void StopAll(TimeSpan wait)
    {
        List<SessionRecorder> all;
        lock (_lock)
        {
            all = _active.ToList();
        }

        foreach (var recorder in all)
        {
            recorder.Stop();
        }

        Task.WaitAll(all.Select(r => r.Completion).ToArray(), wait);
    }

    /// <summary>Space all recordings use now.</summary>
    public long TotalBytes() => RecordingRetention.List(Folder).Sum(f => f.Length);

    sealed class Release(Action release) : IDisposable
    {
        int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                release();
            }
        }
    }
}

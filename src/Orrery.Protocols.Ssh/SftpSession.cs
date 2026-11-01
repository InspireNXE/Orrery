using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Orrery.Protocols.Ssh;

/// <summary>One file or folder on the server.</summary>
/// <param name="Permissions">Unix style, like "drwxr-xr-x".</param>
public sealed record SftpItem(string Name, string FullPath, bool IsDirectory, bool IsLink, long Size, DateTime Modified, string Permissions);

/// <summary>
/// Files on an SSH server over SFTP: the same login, agent keys, host-key check and jump hosts as a shell, on a
/// connection of its own. Transfers report progress and stop when canceled; folders go across with what's in them.
/// Every operation throws <see cref="SftpException"/> with a message for the user when it fails.
/// </summary>
public sealed class SftpSession : IConnectionSession
{
    static readonly ILogger Logger = Log.For("Sftp");

    readonly HostKeyValidator _hostKeys;
    readonly bool _allowLegacy;
    readonly IReadOnlyList<SshHop> _jumps;
    readonly object _lock = new();
    SshTunnel? _tunnel;
    SftpClient? _client;
    bool _closing;

    SftpSession(ConnectionTarget target, HostKeyValidator hostKeys, bool allowLegacy, IReadOnlyList<SshHop> jumps)
    {
        Target = target;
        _hostKeys = hostKeys;
        _allowLegacy = allowLegacy;
        _jumps = jumps;
    }

    public ConnectionTarget Target { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;

    /// <summary>True when the server turned down every way of logging in that was tried.</summary>
    public bool AuthenticationRejected { get; private set; }

    /// <summary>The folder the server starts in (usually the user's home), once connected.</summary>
    public string HomeDirectory { get; private set; } = "/";

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public SessionStateChangedEventArgs? LastChange { get; private set; }

    /// <summary>Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>.</summary>
    public static SftpSession Start(ConnectionTarget target, SshLogin login, HostKeyValidator hostKeys, bool allowLegacy = false, IReadOnlyList<SshHop>? jumps = null)
    {
        var session = new SftpSession(target, hostKeys, allowLegacy, jumps ?? []);
        _ = Task.Run(() => session.Run(login));
        return session;
    }

    void Run(SshLogin login)
    {
        string host = Target.Host;
        int port = Target.Port;
        HostKeyCheck? hostKey = null;
        try
        {
            if (_jumps.Count > 0)
            {
                var tunnel = new SshTunnel(_jumps, _hostKeys);
                lock (_lock)
                {
                    if (_closing) return;
                    _tunnel = tunnel;
                }

                (host, port) = tunnel.Open(Target.Host, Target.Port);
            }

            var client = new SftpClient(SshSession.ConnectionInfoFor(host, port, login, _allowLegacy)) { KeepAliveInterval = SshSession.KeepAlive };
            hostKey = HostKeyCheck.Attach(client, _hostKeys, Target);
            client.ErrorOccurred += (_, e) => Fail(SshSession.Describe(e.Exception, _allowLegacy), SshSession.Recognize(e.Exception));
            lock (_lock)
            {
                if (_closing)
                {
                    client.Dispose();
                    return;
                }

                _client = client;
            }

            hostKey.Connect(client);
            HomeDirectory = client.WorkingDirectory;
            SetState(SessionState.Connected);
        }
        catch (OperationCanceledException)
        {
            // Closed while connecting.
        }
        catch (RouteException e)
        {
            Fail(e.Message, e);
        }
        catch (SshConnectionException e) when (hostKey?.Rejected == true)
        {
            Fail("The server's host key was not trusted.", hostKey.Failure("The server's host key was not trusted.", e));
        }
        catch (SshAuthenticationException e)
        {
            AuthenticationRejected = true;
            Fail(SshSession.Refusal(e, login), SshSession.Recognize(e));
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            Log.For("Sftp").LogError(e, "SFTP session to {Host} failed", Target.Host);
            Fail(e is SftpPathNotFoundException ? "The server has no SFTP subsystem." : SshSession.Describe(e, _allowLegacy), SshSession.Recognize(e));
        }
    }

    SftpClient Client
    {
        get
        {
            lock (_lock)
            {
                return _client is { IsConnected: true } client && State == SessionState.Connected
                    ? client
                    : throw new SftpException("The connection is closed.");
            }
        }
    }

    /// <summary>What's in <paramref name="path"/>, without "." and "..", folders first and then by name.</summary>
    public Task<IReadOnlyList<SftpItem>> ListAsync(string path, CancellationToken token = default) => Run(async () =>
    {
        var items = new List<SftpItem>();
        await foreach (var file in Client.ListDirectoryAsync(path, token))
        {
            if (file.Name is not ("." or ".."))
            {
                items.Add(ItemFor(file));
            }
        }

        return (IReadOnlyList<SftpItem>)[.. Sorted(items)];
    });

    /// <summary>Folders first, then by name, ignoring case.</summary>
    public static IEnumerable<SftpItem> Sorted(IEnumerable<SftpItem> items) =>
        items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies <paramref name="items"/> into the local folder <paramref name="localFolder"/>, each under its
    /// <see cref="SftpItem.Name"/> and folders with everything in them. <paramref name="progress"/> gets the bytes
    /// copied so far across all of them.
    /// </summary>
    public Task DownloadAsync(IReadOnlyList<SftpItem> items, string localFolder, IProgress<long>? progress = null, CancellationToken token = default) => Run(async () =>
    {
        long done = 0;
        int files = 0;
        var took = System.Diagnostics.Stopwatch.StartNew();
        foreach (var item in items)
        {
            await DownloadIntoAsync(item, localFolder);
        }

        Logger.LogInformation("{Host}: downloaded {Files} files, {Bytes} bytes in {Seconds:0.0} s", Target.Host, files, done, took.Elapsed.TotalSeconds);

        async Task DownloadIntoAsync(SftpItem from, string folder)
        {
            var local = Path.Combine(folder, LocalName(from.Name));
            if (from.IsDirectory)
            {
                Directory.CreateDirectory(local);
                await foreach (var file in Client.ListDirectoryAsync(from.FullPath, token))
                {
                    if (file.Name is not ("." or "..") && !file.IsSymbolicLink)
                    {
                        await DownloadIntoAsync(ItemFor(file), local);
                    }
                }

                return;
            }

            long before = done;
            try
            {
                await using var output = File.Create(local);
                await Client.DownloadFileAsync(from.FullPath, output,
                    new Progress<DownloadFileProgressReport>(r => progress?.Report(done = before + (long)r.TotalBytesDownloaded)), token);
            }
            catch (OperationCanceledException)
            {
                // A half-copied file would look complete.
                File.Delete(local);
                throw;
            }

            done = before + from.Size;
            files++;
            Logger.LogDebug("{Host}: downloaded {Remote} to {Local}", Target.Host, from.FullPath, local);
            progress?.Report(done);
        }
    });

    /// <summary>
    /// Copies the local files and folders <paramref name="localPaths"/> into <paramref name="remoteFolder"/>, folders
    /// with everything in them, replacing files of the same name. <paramref name="progress"/> gets the bytes copied
    /// so far across all of them.
    /// </summary>
    public Task UploadAsync(IReadOnlyList<string> localPaths, string remoteFolder, IProgress<long>? progress = null, CancellationToken token = default) => Run(async () =>
    {
        long done = 0;
        int files = 0;
        var took = System.Diagnostics.Stopwatch.StartNew();
        foreach (var path in localPaths)
        {
            await UploadIntoAsync(path, remoteFolder);
        }

        Logger.LogInformation("{Host}: uploaded {Files} files, {Bytes} bytes in {Seconds:0.0} s", Target.Host, files, done, took.Elapsed.TotalSeconds);

        async Task UploadIntoAsync(string from, string folder)
        {
            var remote = Combine(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(from)));
            if (Directory.Exists(from))
            {
                if (!await Client.ExistsAsync(remote, token))
                {
                    await Client.CreateDirectoryAsync(remote, token);
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(from))
                {
                    await UploadIntoAsync(entry, remote);
                }

                return;
            }

            long before = done;
            await using var input = File.OpenRead(from);
            await Client.UploadFileAsync(input, remote, canOverride: true,
                new Progress<UploadFileProgressReport>(r => progress?.Report(done = before + (long)r.TotalBytesUploaded)), token);
            done = before + input.Length;
            files++;
            Logger.LogDebug("{Host}: uploaded {Local} to {Remote}", Target.Host, from, remote);
            progress?.Report(done);
        }
    });

    /// <summary>Total bytes in the local files and folders, for a progress bar.</summary>
    public static long LocalSize(IEnumerable<string> paths) => paths.Sum(path =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : File.Exists(path) ? new FileInfo(path).Length : 0);

    /// <summary>Total bytes in the server's files and folders, for a progress bar.</summary>
    public Task<long> RemoteSizeAsync(IEnumerable<SftpItem> items, CancellationToken token = default) => Run(async () =>
    {
        long total = 0;
        foreach (var item in items)
        {
            total += await SizeOfAsync(item);
        }

        return total;

        async Task<long> SizeOfAsync(SftpItem item)
        {
            if (!item.IsDirectory) return item.Size;
            long size = 0;
            await foreach (var file in Client.ListDirectoryAsync(item.FullPath, token))
            {
                if (file.Name is not ("." or "..") && !file.IsSymbolicLink)
                {
                    size += await SizeOfAsync(ItemFor(file));
                }
            }

            return size;
        }
    });

    public Task CreateFolderAsync(string path, CancellationToken token = default) => Run(async () =>
    {
        await Client.CreateDirectoryAsync(path, token);
        Logger.LogDebug("{Host}: made folder {Path}", Target.Host, path);
    });

    public Task RenameAsync(string from, string to, CancellationToken token = default) => Run(async () =>
    {
        await Client.RenameFileAsync(from, to, token);
        Logger.LogDebug("{Host}: renamed {From} to {To}", Target.Host, from, to);
    });

    /// <summary>Deletes a file, or a folder and everything in it. A link is removed, never what it points to.</summary>
    public Task DeleteAsync(SftpItem item, CancellationToken token = default) => Run(async () =>
    {
        await DeleteTreeAsync(item);
        Logger.LogDebug("{Host}: deleted {Path}", Target.Host, item.FullPath);

        async Task DeleteTreeAsync(SftpItem target)
        {
            if (target.IsDirectory && !target.IsLink)
            {
                var children = new List<SftpItem>();
                await foreach (var file in Client.ListDirectoryAsync(target.FullPath, token))
                {
                    if (file.Name is not ("." or ".."))
                    {
                        children.Add(ItemFor(file));
                    }
                }

                foreach (var child in children)
                {
                    await DeleteTreeAsync(child);
                }

                await Client.DeleteDirectoryAsync(target.FullPath, token);
            }
            else
            {
                await Client.DeleteFileAsync(target.FullPath, token);
            }
        }
    });

    /// <summary>A server path under <paramref name="folder"/>; SFTP paths always use forward slashes.</summary>
    public static string Combine(string folder, string name) => folder.EndsWith('/') ? folder + name : $"{folder}/{name}";

    /// <summary>The folder above <paramref name="path"/>; the root is its own parent.</summary>
    public static string Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    /// <summary>
    /// A server name made safe for this computer: characters Windows doesn't allow become "_", and "." or ".."
    /// can't climb out of the folder being downloaded into.
    /// </summary>
    public static string LocalName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' ? '_' : c).ToArray()).TrimEnd(' ', '.');
        return safe is "" or "." or ".." ? "_" : safe;
    }

    static SftpItem ItemFor(ISftpFile file) => new(file.Name, file.FullName, file.IsDirectory, file.IsSymbolicLink, file.Length, file.LastWriteTime,
        Permissions(file));

    static string Permissions(ISftpFile f) => new(
    [
        f.IsDirectory ? 'd' : f.IsSymbolicLink ? 'l' : '-',
        f.OwnerCanRead ? 'r' : '-', f.OwnerCanWrite ? 'w' : '-', f.OwnerCanExecute ? 'x' : '-',
        f.GroupCanRead ? 'r' : '-', f.GroupCanWrite ? 'w' : '-', f.GroupCanExecute ? 'x' : '-',
        f.OthersCanRead ? 'r' : '-', f.OthersCanWrite ? 'w' : '-', f.OthersCanExecute ? 'x' : '-',
    ]);

    /// <summary>Runs an operation, turning SSH.NET's and the file system's failures into <see cref="SftpException"/>.</summary>
    async Task<T> Run<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SftpPermissionDeniedException e)
        {
            throw new SftpException("Permission denied.", e);
        }
        catch (SftpPathNotFoundException e)
        {
            throw new SftpException("That file or folder no longer exists.", e);
        }
        catch (SshConnectionException e)
        {
            Fail("The connection was lost.", SshSession.Recognize(e));
            throw new SftpException("The connection was lost.", e);
        }
        catch (Exception e) when (e is SshException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new SftpException(e.Message, e);
        }
    }

    Task Run(Func<Task> operation) => Run(async () =>
    {
        await operation();
        return true;
    });

    public Task DisconnectAsync()
    {
        SftpClient? client;
        SshTunnel? tunnel;
        lock (_lock)
        {
            _closing = true;
            (client, tunnel) = (_client, _tunnel);
            (_client, _tunnel) = (null, null);
        }

        return Task.Run(() =>
        {
            try
            {
                if (client?.IsConnected == true) client.Disconnect();
            }
            catch (Exception e) when (e is SshException or ObjectDisposedException or IOException)
            {
                // Already gone.
            }
            finally
            {
                client?.Dispose();
                tunnel?.Dispose();
                SetState(SessionState.Disconnected);
            }
        });
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    void Fail(string message, Exception? error = null)
    {
        if (State is SessionState.Disconnected or SessionState.Failed) return;
        SetState(SessionState.Failed, message, error);
    }

    void SetState(SessionState state, string? message = null, Exception? error = null)
    {
        lock (_lock)
        {
            if (State == state || State is SessionState.Disconnected or SessionState.Failed && state != SessionState.Connecting) return;
            State = state;
        }

        var change = new SessionStateChangedEventArgs(state, message, error);
        LastChange = change;
        StateChanged?.Invoke(this, change);
    }
}

/// <summary>An SFTP operation failed; the message says why, for the user.</summary>
public sealed class SftpException(string message, Exception? inner = null) : Exception(message, inner);

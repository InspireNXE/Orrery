using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Orrery.Core.Sessions;

namespace Orrery.Protocols.Rdp;

/// <summary>What the RDP clipboard channel reported, copied out of IronRDP.</summary>
enum RdpClipboardEventKind
{
    /// <summary>The channel finished starting.</summary>
    Ready,

    /// <summary>The channel wants the local formats; it doesn't finish starting until it has them.</summary>
    FormatListRequested,

    /// <summary>The server's clipboard changed; <see cref="RdpClipboardEvent.Formats"/> has what it offers.</summary>
    RemoteFormatList,

    /// <summary>The server wants local data in <see cref="RdpClipboardEvent.Format"/>.</summary>
    FormatDataRequested,

    /// <summary>The server sent the data of a format we asked for.</summary>
    FormatDataResponse,

    /// <summary>The server sent the file list we asked for: <see cref="RdpClipboardEvent.Files"/>.</summary>
    RemoteFileList,

    /// <summary>The server wants the size or bytes of an offered file: <see cref="RdpClipboardEvent.FileRequest"/>.</summary>
    FileContentsRequested,

    /// <summary>The server answered our file contents request <see cref="RdpClipboardEvent.StreamId"/>.</summary>
    FileContentsResponse,

    /// <summary>The server locked the files we offered, as <see cref="RdpClipboardEvent.LockId"/>.</summary>
    Lock,

    /// <summary>The server released the lock <see cref="RdpClipboardEvent.LockId"/>.</summary>
    Unlock,

    /// <summary>Something the bridge doesn't use.</summary>
    Other,
}

/// <param name="Format">The requested format, or for a response the format IronRDP says it answers (0 when not known).</param>
/// <param name="Data">The server's data, or null when it sent a failure or more than the bridge accepts.</param>
sealed record RdpClipboardEvent(RdpClipboardEventKind Kind, IReadOnlyList<uint>? Formats = null, uint Format = 0, byte[]? Data = null)
{
    /// <summary>The names of <see cref="Formats"/>, in the same order; null for one without a name.</summary>
    public IReadOnlyList<string?>? Names { get; init; }

    public IReadOnlyList<RdpRemoteFile>? Files { get; init; }

    public RdpFileRequest? FileRequest { get; init; }

    public uint StreamId { get; init; }

    /// <summary>The lock of a remote file list, or of a lock or unlock.</summary>
    public uint? LockId { get; init; }
}

/// <summary>A clipboard format to announce: a standard one by its id, or a registered one by its name.</summary>
sealed record RdpClipboardFormat(uint Id, string? Name = null);

/// <summary>The RDP clipboard channel as the bridge sees it: IronRDP's callback backend, or a fake in tests.</summary>
interface IRdpClipboardChannel
{
    /// <summary>The next event, or null if none came within <paramref name="timeout"/>.</summary>
    RdpClipboardEvent? NextEvent(TimeSpan timeout);

    /// <summary>Whether this connection transfers files; known once the channel is ready.</summary>
    bool FileTransfer => false;

    /// <summary>Tells the server which formats the local clipboard has; an empty list when it has none.</summary>
    Task AnnounceFormatsAsync(IReadOnlyList<RdpClipboardFormat> formats, CancellationToken token);

    /// <summary>Asks the server for its clipboard data in <paramref name="format"/>.</summary>
    Task RequestDataAsync(uint format, CancellationToken token);

    /// <summary>Answers the server's request; null says there's no data.</summary>
    Task SendDataAsync(byte[]? data, CancellationToken token);

    /// <summary>Tells the server the local clipboard holds <paramref name="files"/>.</summary>
    Task OfferFilesAsync(IReadOnlyList<LocalClipboardFile> files, CancellationToken token) => Task.CompletedTask;

    /// <summary>Asks the server for the size of its file <paramref name="index"/>, or <paramref name="length"/> bytes of it from <paramref name="position"/>.</summary>
    Task RequestFileContentsAsync(uint streamId, int index, bool sizeRequest, long position, int length, uint? lockId, CancellationToken token) => Task.CompletedTask;

    /// <summary>Answers the server's file contents request with bytes; null says it can't be served.</summary>
    Task SendFileContentsAsync(uint streamId, byte[]? data, CancellationToken token) => Task.CompletedTask;

    /// <summary>Answers the server's size request.</summary>
    Task SendFileSizeAsync(uint streamId, long size, CancellationToken token) => Task.CompletedTask;

    /// <summary>Lets the channel expire stale locks and file transfers.</summary>
    Task DriveTimeoutsAsync(CancellationToken token) => Task.CompletedTask;
}

/// <summary>
/// Shares the clipboard over RDP the way VNC does: the view hands over the local text, image or files when the
/// session gets the keyboard, and what the server copies is raised for the view to put on the local clipboard. Text
/// goes as CF_UNICODETEXT, with CF_TEXT for servers that only offer or ask for that; images as CF_DIB and PNG; files
/// through the file list and file contents requests. Files the server copies are downloaded at once into a private
/// folder, since the local clipboard can only hold files that exist. Server data is untrusted: it is capped, names
/// are checked before they become paths, and none of it is logged.
/// </summary>
sealed class RdpClipboardBridge
{
    public const uint CfText = 1;
    public const uint CfDib = 8;
    public const uint CfUnicodeText = 13;

    /// <summary>The id the local PNG format is announced with; a registered format's id is only a handle for its name.</summary>
    public const uint LocalPngFormat = 0xC0A0;

    public const string PngName = "PNG";
    public const string FileListName = "FileGroupDescriptorW";

    /// <summary>The longest text shared either way, in characters.</summary>
    public const int MaxTextLength = 1024 * 1024;

    /// <summary>The longest text as UTF-16 with its null.</summary>
    public const int MaxTextBytes = (MaxTextLength + 1) * 2;

    /// <summary>The most server data accepted for one format or file contents response: an image, the largest.</summary>
    public const int MaxDataBytes = (int)(RdpDib.MaxPixels * 4) + 1024;

    /// <summary>The most bytes of files the server copies that are downloaded.</summary>
    public const long MaxDownloadBytes = 1L << 30;

    /// <summary>How much of a file one request asks for.</summary>
    internal const int FileChunk = 1024 * 1024;

    /// <summary>Paste requests the server hasn't answered yet. More would only be stale.</summary>
    internal const int MaxPendingRequests = 4;

    /// <summary>Locks of offered files kept; the server releases them, so more are only a server that doesn't.</summary>
    const int MaxLocks = 8;

    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan TimeoutInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    static readonly ILogger Logger = Orrery.Core.Diagnostics.Log.For("Rdp");

    enum Kind { Text, Png, Dib, Files }

    readonly IRdpClipboardChannel _channel;
    readonly object _localLock = new();
    // Each with the copy it's for, counted as in _copies.
    readonly Queue<(uint Format, Kind Kind, int Copies)> _requested = new();

    // One of these is set at a time: what the local clipboard last held.
    string? _localText;
    ClipboardImage? _localImage;
    byte[]? _localDib;
    IReadOnlyList<LocalClipboardFile>? _localFiles;
    int _localVersion;
    int _announcedVersion;
    bool _ready;

    // The files last offered to the server, and what it locked of earlier offers.
    IReadOnlyList<LocalClipboardFile> _offered = [];
    readonly Dictionary<uint, IReadOnlyList<LocalClipboardFile>> _locked = [];
    readonly Queue<uint> _lockOrder = new();

    Download? _download;
    string? _lastDownload;
    volatile bool _cancelDownload;
    uint _nextStreamId = 1;
    long _lastTimeouts = Stopwatch.GetTimestamp();

    // Copies made on either side, counted so a download that ends after a newer copy doesn't put its files over it.
    // Under _localLock.
    int _copies;

    // The server's files copied while another download was going: started once that one ends, unless something newer
    // was copied by then. Only the newest waits.
    (IReadOnlyList<RdpRemoteFile> Files, uint? LockId, int Copies)? _queued;

    // Downloads that ended after a newer copy, offered instead of put on the clipboard. Kept until the session ends,
    // the oldest deleted past MaxKept.
    readonly Queue<string> _kept = new();
    const int MaxKept = 4;

    readonly Func<bool> _keepRunning;

    /// <param name="keepRunning">Checked between events; the loop ends when it turns false, such as when the session ends.</param>
    public RdpClipboardBridge(IRdpClipboardChannel channel, Func<bool>? keepRunning = null)
    {
        _channel = channel;
        _keepRunning = keepRunning ?? (() => true);
    }

    /// <summary>The server copied text. Raised on the bridge's thread.</summary>
    public event EventHandler<string>? RemoteTextChanged;

    /// <summary>The server copied an image, as PNG or BMP. Raised on the bridge's thread.</summary>
    public event EventHandler<byte[]>? RemoteImageChanged;

    /// <summary>The server copied files, now downloaded: the paths of the copied items. Raised on the bridge's thread.</summary>
    public event EventHandler<IReadOnlyList<string>>? RemoteFilesChanged;

    /// <summary>
    /// The server's files finished downloading after something newer was copied, so they weren't put on the clipboard
    /// over it: the paths of the copied items, kept until the session ends. Raised on the bridge's thread.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? RemoteFilesReady;

    /// <summary>How a download of the server's files is going. Raised on the bridge's thread.</summary>
    public event EventHandler<ClipboardTransfer>? TransferChanged;

    /// <summary>Something on the clipboard wasn't shared, with a sentence saying why. Raised on the thread that refused it.</summary>
    public event EventHandler<string>? TextRefused;

    /// <summary>The server's clipboard text was <paramref name="bytes"/> long, too long to take.</summary>
    internal void RefuseRemote(long bytes)
    {
        Logger.LogWarning("Server clipboard text not used: {Length} bytes is more than {Max}", bytes, MaxTextBytes);
        TextRefused?.Invoke(this, $"Clipboard text from the remote desktop wasn't copied here: it's over the {MaxTextLength:N0}-character limit.");
    }

    /// <summary>Offers <paramref name="text"/> to the server. Any thread; the bridge's loop tells the server.</summary>
    public void SetLocalText(string text)
    {
        if (text.Length > MaxTextLength)
        {
            Logger.LogWarning("Local clipboard text not shared: {Length} characters is more than {Max}", text.Length, MaxTextLength);
            TextRefused?.Invoke(this, $"Clipboard text wasn't sent to the remote desktop: {text.Length:N0} characters is over the {MaxTextLength:N0}-character limit.");
            return;
        }

        lock (_localLock)
        {
            if (text == _localText)
            {
                return;
            }

            SetLocal(text: text);
            _copies++;
        }
    }

    /// <summary>Offers <paramref name="image"/> to the server. Any thread.</summary>
    public void SetLocalImage(ClipboardImage image)
    {
        if ((long)image.Width * image.Height > RdpDib.MaxPixels)
        {
            Logger.LogWarning("Local clipboard image not shared: {Width}x{Height} is too large", image.Width, image.Height);
            TextRefused?.Invoke(this, $"The copied image wasn't sent to the remote desktop: at {image.Width:N0} by {image.Height:N0} pixels it's too large.");
            return;
        }

        var dib = RdpDib.FromBgra(image.Width, image.Height, image.Bgra);
        lock (_localLock)
        {
            SetLocal(image: image, dib: dib);
            _copies++;
        }
    }

    /// <summary>Offers the files and folders at <paramref name="paths"/> to the server. Any thread.</summary>
    public void SetLocalFiles(IReadOnlyList<string> paths)
    {
        if (RdpClipboardFiles.Collect(paths) is not { } files)
        {
            Logger.LogWarning("Local clipboard files not shared: more than {Max}", RdpClipboardFiles.MaxEntries);
            TextRefused?.Invoke(this, $"The copied files weren't sent to the remote desktop: there are more than {RdpClipboardFiles.MaxEntries:N0}.");
            return;
        }

        lock (_localLock)
        {
            SetLocal(files: files);
            _copies++;
        }
    }

    int CopyCount()
    {
        lock (_localLock)
        {
            return _copies;
        }
    }

    void SetLocal(string? text = null, ClipboardImage? image = null, byte[]? dib = null, IReadOnlyList<LocalClipboardFile>? files = null)
    {
        _localText = text;
        _localImage = image;
        _localDib = dib;
        _localFiles = files;
        _localVersion++;
    }

    /// <summary>Stops a download of the server's files. Any thread; the bridge's loop stops it.</summary>
    public void CancelDownload() => _cancelDownload = true;

    /// <summary>Deletes the files downloaded, for when the session ends. Call once the loop has stopped.</summary>
    public void DeleteDownloads()
    {
        _download?.Close();
        RdpClipboardFiles.Delete(_download?.Root);
        _download = null;
        _queued = null;
        RdpClipboardFiles.Delete(_lastDownload);
        _lastDownload = null;
        while (_kept.TryDequeue(out var kept))
        {
            RdpClipboardFiles.Delete(kept);
        }
    }

    /// <summary>Handles the channel's events until <paramref name="token"/> is canceled.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _keepRunning())
        {
            // Blocks the thread for up to PollInterval, so the session runs this on a thread of its own (OwnThread).
            var next = _channel.NextEvent(PollInterval);
            if (next is not null)
            {
                await HandleAsync(next, token);
            }

            if (_cancelDownload)
            {
                // Cancel stops what's waiting too: the strip showed one download, and both go.
                _cancelDownload = false;
                _queued = null;
                EndDownload(null);
            }

            await AnnounceIfChangedAsync(token);

            if (_ready && _channel.FileTransfer && Stopwatch.GetElapsedTime(_lastTimeouts) >= TimeoutInterval)
            {
                _lastTimeouts = Stopwatch.GetTimestamp();
                await _channel.DriveTimeoutsAsync(token);
            }
        }
    }

    internal async Task HandleAsync(RdpClipboardEvent clipboardEvent, CancellationToken token)
    {
        switch (clipboardEvent.Kind)
        {
            case RdpClipboardEventKind.FormatListRequested:
                await AnnounceAsync(token);
                break;

            case RdpClipboardEventKind.Ready:
                _ready = true;
                break;

            case RdpClipboardEventKind.RemoteFormatList:
                await OnRemoteFormatsAsync(clipboardEvent.Formats ?? [], clipboardEvent.Names, token);
                break;

            case RdpClipboardEventKind.FormatDataRequested:
                await _channel.SendDataAsync(Encode(clipboardEvent.Format), token);
                break;

            case RdpClipboardEventKind.FormatDataResponse:
                OnRemoteData(clipboardEvent.Format, clipboardEvent.Data);
                break;

            case RdpClipboardEventKind.RemoteFileList:
                await OnRemoteFilesAsync(clipboardEvent.Files ?? [], clipboardEvent.LockId, token);
                break;

            case RdpClipboardEventKind.FileContentsRequested when clipboardEvent.FileRequest is { } request:
                await ServeAsync(request, token);
                break;

            case RdpClipboardEventKind.FileContentsResponse:
                await OnFileContentsAsync(clipboardEvent.StreamId, clipboardEvent.Data, token);
                break;

            case RdpClipboardEventKind.Lock when clipboardEvent.LockId is { } id:
                if (_lockOrder.Count == MaxLocks)
                {
                    _locked.Remove(_lockOrder.Dequeue());
                }

                _locked[id] = _offered;
                _lockOrder.Enqueue(id);
                break;

            case RdpClipboardEventKind.Unlock when clipboardEvent.LockId is { } id:
                _locked.Remove(id);
                break;
        }

        // A copy that waited for another download starts once that one has ended, if it's still the newest.
        if (_download is null && _queued is { } queued)
        {
            _queued = null;
            if (queued.Copies == CopyCount())
            {
                await StartDownloadAsync(queued.Files, queued.LockId, queued.Copies, token);
            }
        }
    }

    /// <summary>
    /// Tells the server about a local copy, once no download of its files is going: a Windows server fails the rest of
    /// a download, lock or no lock, as soon as the client announces a new clipboard. Internal for tests.
    /// </summary>
    internal async Task AnnounceIfChangedAsync(CancellationToken token)
    {
        if (_ready && _download is null && LocalChangedSinceAnnounced())
        {
            await AnnounceAsync(token);
        }
    }

    async Task AnnounceAsync(CancellationToken token)
    {
        int version;
        bool hasText, hasImage;
        IReadOnlyList<LocalClipboardFile>? files;
        lock (_localLock)
        {
            version = _localVersion;
            hasText = !string.IsNullOrEmpty(_localText);
            hasImage = _localImage is not null;
            files = _localFiles;
        }

        if (files is { Count: > 0 } && _channel.FileTransfer)
        {
            _offered = files;
            await _channel.OfferFilesAsync(files, token);
        }
        else
        {
            // Files no longer on the clipboard stop being readable, except what the server locked while they were.
            _offered = [];
            await _channel.AnnounceFormatsAsync(
                hasText ? [new(CfUnicodeText), new(CfText)]
                : hasImage ? [new(CfDib), new(LocalPngFormat, PngName)]
                : [],
                token);
        }

        lock (_localLock)
        {
            _announcedVersion = version;
        }
    }

    bool LocalChangedSinceAnnounced()
    {
        lock (_localLock)
        {
            return _localVersion != _announcedVersion;
        }
    }

    async Task OnRemoteFormatsAsync(IReadOnlyList<uint> formats, IReadOnlyList<string?>? names, CancellationToken token)
    {
        uint? Named(string name)
        {
            for (int i = 0; names is not null && i < Math.Min(formats.Count, names.Count); i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return formats[i];
                }
            }

            return null;
        }

        // Text wins over an image: Office copies text with a picture of it.
        (uint Format, Kind Kind)? wanted =
            _channel.FileTransfer && Named(FileListName) is { } fileList ? (fileList, Kind.Files)
            : formats.Contains(CfUnicodeText) ? (CfUnicodeText, Kind.Text)
            : formats.Contains(CfText) ? (CfText, Kind.Text)
            : Named(PngName) is { } png ? (png, Kind.Png)
            : formats.Contains(CfDib) ? (CfDib, Kind.Dib)
            : null;
        if (wanted is not { } request)
        {
            return;
        }

        // A server that never answers can't make this grow: the oldest request is forgotten.
        if (_requested.Count == MaxPendingRequests)
        {
            _requested.Dequeue();
        }

        // The server's copy replaces the local clipboard when it arrives, so it counts as a copy now.
        int copies;
        lock (_localLock)
        {
            copies = ++_copies;
        }

        _requested.Enqueue((request.Format, request.Kind, copies));
        await _channel.RequestDataAsync(request.Format, token);
    }

    void OnRemoteData(uint reportedFormat, byte[]? data)
    {
        // Responses come in the order they were asked for. IronRDP also names the format each one answers; when it
        // does, that wins over this queue, which may have forgotten a request.
        if (!_requested.TryDequeue(out var request) || data is null)
        {
            return;
        }

        var kind = request.Kind;
        var format = request.Format;
        if (reportedFormat != 0 && reportedFormat != format)
        {
            format = reportedFormat;
            kind = format is CfText or CfUnicodeText ? Kind.Text : format == CfDib ? Kind.Dib : kind;
        }

        switch (kind)
        {
            case Kind.Text:
                if (data.Length > MaxTextBytes)
                {
                    RefuseRemote(data.Length);
                    return;
                }

                if (Decode(format, data) is not { } text)
                {
                    return;
                }

                lock (_localLock)
                {
                    // What came from the server needn't be offered back to it.
                    SetLocal(text: text);
                    _announcedVersion = _localVersion;
                }

                RemoteTextChanged?.Invoke(this, text);
                break;

            case Kind.Png:
                // Decoding is sized by the header, not by the bytes sent, so a small file can claim a huge picture.
                if (RdpDib.PngWithinLimit(data))
                {
                    ForgetLocal();
                    RemoteImageChanged?.Invoke(this, data);
                }
                break;

            case Kind.Dib:
                if (RdpDib.ToBmpFile(data) is { } bmp)
                {
                    ForgetLocal();
                    RemoteImageChanged?.Invoke(this, bmp);
                }
                break;
        }
    }

    /// <summary>The server's clipboard replaced the local one, so nothing local is offered until the next local copy.</summary>
    void ForgetLocal()
    {
        lock (_localLock)
        {
            SetLocal();
            _announcedVersion = _localVersion;
        }
    }

    byte[]? Encode(uint format)
    {
        string? text;
        ClipboardImage? image;
        byte[]? dib;
        lock (_localLock)
        {
            text = _localText;
            image = _localImage;
            dib = _localDib;
        }

        if (format == CfDib)
        {
            return dib;
        }

        if (format == LocalPngFormat)
        {
            return image?.Png;
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return format switch
        {
            CfUnicodeText => Encoding.Unicode.GetBytes(text + '\0'),

            // CF_TEXT is in the server's ANSI code page, which isn't known here; Latin-1 is right for the common
            // Western one and turns everything else into '?'.
            CfText => Encoding.Latin1.GetBytes(text + '\0'),
            _ => null,
        };
    }

    /// <summary>The server's text, or null when it's too long or empty.</summary>
    internal static string? Decode(uint format, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxTextBytes)
        {
            Logger.LogWarning("Server clipboard text not used: {Length} bytes is more than {Max}", data.Length, MaxTextBytes);
            return null;
        }

        string text;
        if (format == CfUnicodeText)
        {
            // An odd last byte is no character; unpaired surrogates become U+FFFD.
            var units = data[..(data.Length & ~1)];
            int end = 0;
            while (end < units.Length && (units[end] != 0 || units[end + 1] != 0))
            {
                end += 2;
            }

            text = Encoding.Unicode.GetString(units[..end]);
        }
        else
        {
            int end = data.IndexOf((byte)0);
            text = Encoding.Latin1.GetString(end < 0 ? data : data[..end]);
        }

        return text.Length is > 0 and <= MaxTextLength ? text : null;
    }

    async Task ServeAsync(RdpFileRequest request, CancellationToken token)
    {
        var files = request.LockId is { } id && _locked.TryGetValue(id, out var locked) ? locked : _offered;
        if (request.Index < 0 || request.Index >= files.Count || files[request.Index].Directory)
        {
            await _channel.SendFileContentsAsync(request.StreamId, null, token);
            return;
        }

        var path = files[request.Index].Path;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (request.SizeRequest)
            {
                await _channel.SendFileSizeAsync(request.StreamId, stream.Length, token);
                return;
            }

            var buffer = new byte[(int)Math.Clamp(Math.Min(request.Length, stream.Length - request.Position), 0, FileChunk * 4)];
            stream.Position = Math.Min(request.Position, stream.Length);
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            await _channel.SendFileContentsAsync(request.StreamId, buffer.AsSpan(0, read).ToArray(), token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Couldn't read a copied file for the server: {Message}", e.Message);
            await _channel.SendFileContentsAsync(request.StreamId, null, token);
        }
    }

    async Task OnRemoteFilesAsync(IReadOnlyList<RdpRemoteFile> files, uint? lockId, CancellationToken token)
    {
        int copies = _requested.TryDequeue(out var asked) ? asked.Copies : CopyCount();

        // A download going finishes first: the server keeps its files under their lock after the clipboard changes, so
        // copying again doesn't throw it away. This copy waits, and replaces one already waiting.
        if (_download is not null)
        {
            _queued = files.Count == 0 ? null : (files, lockId, copies);
            return;
        }

        await StartDownloadAsync(files, lockId, copies, token);
    }

    async Task StartDownloadAsync(IReadOnlyList<RdpRemoteFile> files, uint? lockId, int copies, CancellationToken token)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (files.Count > RdpClipboardFiles.MaxEntries)
        {
            Refuse($"The copied files weren't copied here: there are more than {RdpClipboardFiles.MaxEntries:N0}.");
            return;
        }

        // Each size is clamped first, so a list of huge ones can't wrap the total around to under the limit.
        long total = files.Where(f => !f.Directory).Sum(f => Math.Clamp(f.Size ?? 0, 0, MaxDownloadBytes + 1));
        if (total > MaxDownloadBytes)
        {
            Refuse($"The copied files weren't copied here: together they're over {MaxDownloadBytes / (1 << 30):N0} GB.");
            return;
        }

        string root;
        try
        {
            root = RdpClipboardFiles.CreatePrivateFolder();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Refuse($"The copied files weren't copied here: {e.Message}");
            return;
        }

        var download = new Download(root, lockId, total, copies);
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (RdpClipboardFiles.LocalPath(root, file) is not { } path)
            {
                Logger.LogWarning("Left out a copied file whose name can't be used here");
                continue;
            }

            try
            {
                if (file.Directory)
                {
                    Directory.CreateDirectory(path);
                }
                else
                {
                    // A folder the list leaves out still has to be there.
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A name this file system can't take, such as one over its length in bytes.
                Logger.LogWarning("Left out a copied file that can't be made here: {Message}", e.Message);
                continue;
            }

            // Requests name a file by its index in the server's list, so that is kept with it.
            download.Entries.Add((i, file, path));
        }

        _download = download;
        TransferChanged?.Invoke(this, download.Progress(ended: false));
        await NextRequestAsync(token);
    }

    void Refuse(string reason)
    {
        Logger.LogWarning("Server clipboard files not downloaded: {Reason}", reason);
        TextRefused?.Invoke(this, reason);
    }

    /// <summary>Asks for the next piece of the download, or finishes it when nothing is left.</summary>
    async Task NextRequestAsync(CancellationToken token)
    {
        if (_download is not { } download)
        {
            return;
        }

        while (download.Current < download.Entries.Count)
        {
            var (index, file, path) = download.Entries[download.Current];
            if (file.Directory)
            {
                download.Current++;
                continue;
            }

            if (download.Size is null)
            {
                if (file.Size is { } given)
                {
                    download.Size = given;
                }
                else
                {
                    download.Expected = _nextStreamId++;
                    download.AskedSize = true;
                    await _channel.RequestFileContentsAsync(download.Expected, index, true, 0, 8, download.LockId, token);
                    return;
                }
            }

            // Two entries of the same name: the second is left out rather than written over the first.
            try
            {
                download.Stream ??= new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                download.NextFile();
                continue;
            }

            if (download.Position >= download.Size)
            {
                download.NextFile();
                continue;
            }

            int length = (int)Math.Min(FileChunk, download.Size.Value - download.Position);
            download.Expected = _nextStreamId++;
            download.AskedSize = false;
            download.Asked = length;
            await _channel.RequestFileContentsAsync(download.Expected, index, false, download.Position, length, download.LockId, token);
            return;
        }

        EndDownload(download);
    }

    async Task OnFileContentsAsync(uint streamId, byte[]? data, CancellationToken token)
    {
        if (_download is not { } download || streamId != download.Expected)
        {
            return;
        }

        try
        {
            if (data is null)
            {
                throw new IOException("the remote desktop couldn't send a file");
            }

            if (download.AskedSize)
            {
                if (data.Length < 8)
                {
                    throw new IOException("the remote desktop sent no size");
                }

                long size = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(data), long.MaxValue);
                // Compared before adding, so the total never wraps around.
                if (size > MaxDownloadBytes - download.Total)
                {
                    EndDownload(null);
                    Refuse($"The copied files weren't copied here: together they're over {MaxDownloadBytes / (1 << 30):N0} GB.");
                    return;
                }

                download.Total += size;
                download.Size = size;
            }
            else
            {
                // Short reads are fine, but no bytes would never end, and more than asked for is a broken server.
                if (data.Length == 0 || data.Length > download.Asked)
                {
                    throw new IOException("the remote desktop sent a file's bytes wrongly");
                }

                download.Stream!.Write(data);
                download.Position += data.Length;
                download.Done += data.Length;
                if (Stopwatch.GetElapsedTime(download.LastProgress) >= ProgressInterval)
                {
                    download.LastProgress = Stopwatch.GetTimestamp();
                    TransferChanged?.Invoke(this, download.Progress(ended: false));
                }
            }

            await NextRequestAsync(token);
        }
        catch (IOException e)
        {
            Logger.LogWarning("Download of the server's clipboard files failed: {Message}", e.Message);
            EndDownload(null);
            Refuse("The copied files weren't copied here: the transfer from the remote desktop failed.");
        }
    }

    /// <summary>Ends the download going: finished when <paramref name="finished"/> is it, otherwise thrown away.</summary>
    void EndDownload(Download? finished)
    {
        if (_download is not { } download)
        {
            return;
        }

        _download = null;
        download.Close();
        if (finished is null)
        {
            RdpClipboardFiles.Delete(download.Root);
            TransferChanged?.Invoke(this, download.Progress(ended: true));
            return;
        }

        TransferChanged?.Invoke(this, download.Progress(ended: true));
        var copied = download.Entries.Where(e => e.File.RelativePath.Length == 0).Select(e => e.Path).ToList();
        if (download.Copies != CopyCount())
        {
            // Something newer was copied while these downloaded. It stays on the clipboard, and these are offered.
            _kept.Enqueue(download.Root);
            while (_kept.Count > MaxKept)
            {
                RdpClipboardFiles.Delete(_kept.Dequeue());
            }

            if (copied.Count > 0)
            {
                RemoteFilesReady?.Invoke(this, copied);
            }

            return;
        }

        // The previous files can go now: the clipboard is about to hold these instead.
        RdpClipboardFiles.Delete(_lastDownload);
        _lastDownload = download.Root;
        if (copied.Count > 0)
        {
            ForgetLocal();
            RemoteFilesChanged?.Invoke(this, copied);
        }
    }

    /// <summary>A download of the server's files, one file at a time.</summary>
    sealed class Download(string root, uint? lockId, long total, int copies)
    {
        public string Root { get; } = root;
        public uint? LockId { get; } = lockId;

        /// <summary>The copy these files are, counted as in <c>_copies</c>.</summary>
        public int Copies { get; } = copies;
        public long Total { get; set; } = total;
        public long Done { get; set; }

        /// <summary>Each file kept: its index in the server's list, what the server said of it, and where it goes.</summary>
        public List<(int Index, RdpRemoteFile File, string Path)> Entries { get; } = [];

        /// <summary>The entry being downloaded, and how far.</summary>
        public int Current { get; set; }
        public long? Size { get; set; }
        public long Position { get; set; }
        public FileStream? Stream { get; set; }

        public uint Expected { get; set; }
        public bool AskedSize { get; set; }
        public int Asked { get; set; }
        public long LastProgress { get; set; } = Stopwatch.GetTimestamp();

        public ClipboardTransfer Progress(bool ended) => new(Entries.Count(e => !e.File.Directory), Done, Total, ended);

        public void NextFile()
        {
            Close();
            Current++;
            Size = null;
            Position = 0;
        }

        public void Close()
        {
            Stream?.Dispose();
            Stream = null;
        }
    }
}

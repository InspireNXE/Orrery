using System.Text;
using Microsoft.Extensions.Logging;

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

    /// <summary>Something text-only clipboard sharing doesn't need, such as a lock.</summary>
    Other,
}

/// <param name="Data">The server's data, or null when it sent a failure or more than the bridge accepts.</param>
sealed record RdpClipboardEvent(RdpClipboardEventKind Kind, IReadOnlyList<uint>? Formats = null, uint Format = 0, byte[]? Data = null);

/// <summary>The RDP clipboard channel as the bridge sees it: IronRDP's callback backend, or a fake in tests.</summary>
interface IRdpClipboardChannel
{
    /// <summary>The next event, or null if none came within <paramref name="timeout"/>.</summary>
    RdpClipboardEvent? NextEvent(TimeSpan timeout);

    /// <summary>Tells the server which formats the local clipboard has; an empty list when it has none.</summary>
    Task AnnounceFormatsAsync(IReadOnlyList<uint> formats, CancellationToken token);

    /// <summary>Asks the server for its clipboard data in <paramref name="format"/>.</summary>
    Task RequestDataAsync(uint format, CancellationToken token);

    /// <summary>Answers the server's request; null says there's no data.</summary>
    Task SendDataAsync(byte[]? data, CancellationToken token);
}

/// <summary>
/// Shares clipboard text over RDP the way VNC does: the view hands over the local text when the session gets the
/// keyboard, and text the server copies is raised for the view to put on the local clipboard. Text goes as
/// CF_UNICODETEXT, with CF_TEXT for servers that only offer or ask for that. Server data is untrusted: it is capped,
/// cut at the first null, and decoded with replacement characters, and it is never logged.
/// </summary>
sealed class RdpClipboardBridge
{
    public const uint CfText = 1;
    public const uint CfUnicodeText = 13;

    /// <summary>The longest text shared either way, in characters.</summary>
    public const int MaxTextLength = 1024 * 1024;

    /// <summary>The most server data accepted for one format: the longest text as UTF-16 with its null.</summary>
    public const int MaxDataBytes = (MaxTextLength + 1) * 2;

    /// <summary>Paste requests the server hasn't answered yet. More would only be stale.</summary>
    const int MaxPendingRequests = 4;

    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    static readonly ILogger Logger = Orrery.Core.Diagnostics.Log.For("Rdp");

    readonly IRdpClipboardChannel _channel;
    readonly object _localLock = new();
    readonly Queue<uint> _requested = new();

    string? _localText;
    int _localVersion;
    int _announcedVersion;
    bool _ready;

    readonly Func<bool> _keepRunning;

    /// <param name="keepRunning">Checked between events; the loop ends when it turns false, such as when the session ends.</param>
    public RdpClipboardBridge(IRdpClipboardChannel channel, Func<bool>? keepRunning = null)
    {
        _channel = channel;
        _keepRunning = keepRunning ?? (() => true);
    }

    /// <summary>The server copied text. Raised on the bridge's thread.</summary>
    public event EventHandler<string>? RemoteTextChanged;

    /// <summary>Clipboard text wasn't shared, with a sentence saying why. Raised on the thread that refused it.</summary>
    public event EventHandler<string>? TextRefused;

    /// <summary>The server's clipboard text was <paramref name="bytes"/> long, too long to take.</summary>
    internal void RefuseRemote(long bytes)
    {
        Logger.LogWarning("Server clipboard text not used: {Length} bytes is more than {Max}", bytes, MaxDataBytes);
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

            _localText = text;
            _localVersion++;
        }
    }

    /// <summary>Handles the channel's events until <paramref name="token"/> is canceled.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _keepRunning())
        {
            // Blocks the thread for up to PollInterval, so this runs on a pool thread of its own.
            var next = _channel.NextEvent(PollInterval);
            if (next is not null)
            {
                await HandleAsync(next, token);
            }

            if (_ready && LocalChangedSinceAnnounced())
            {
                await AnnounceAsync(token);
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
                await OnRemoteFormatsAsync(clipboardEvent.Formats ?? [], token);
                break;

            case RdpClipboardEventKind.FormatDataRequested:
                await _channel.SendDataAsync(Encode(clipboardEvent.Format), token);
                break;

            case RdpClipboardEventKind.FormatDataResponse:
                OnRemoteData(clipboardEvent.Data);
                break;
        }
    }

    async Task AnnounceAsync(CancellationToken token)
    {
        int version;
        bool hasText;
        lock (_localLock)
        {
            version = _localVersion;
            hasText = !string.IsNullOrEmpty(_localText);
        }

        await _channel.AnnounceFormatsAsync(hasText ? [CfUnicodeText, CfText] : [], token);
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

    async Task OnRemoteFormatsAsync(IReadOnlyList<uint> formats, CancellationToken token)
    {
        uint? wanted = formats.Contains(CfUnicodeText) ? CfUnicodeText : formats.Contains(CfText) ? CfText : null;
        if (wanted is not { } format)
        {
            return;
        }

        // A server that never answers can't make this grow: the oldest request is forgotten.
        if (_requested.Count == MaxPendingRequests)
        {
            _requested.Dequeue();
        }

        _requested.Enqueue(format);
        await _channel.RequestDataAsync(format, token);
    }

    void OnRemoteData(byte[]? data)
    {
        // The response doesn't say which format it carries; responses come in the order they were asked for.
        if (!_requested.TryDequeue(out var format) || data is null)
        {
            return;
        }

        if (data.Length > MaxDataBytes)
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
            _localText = text;
            _announcedVersion = ++_localVersion;
        }

        RemoteTextChanged?.Invoke(this, text);
    }

    byte[]? Encode(uint format)
    {
        string? text;
        lock (_localLock)
        {
            text = _localText;
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
        if (data.Length > MaxDataBytes)
        {
            Logger.LogWarning("Server clipboard text not used: {Length} bytes is more than {Max}", data.Length, MaxDataBytes);
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
}

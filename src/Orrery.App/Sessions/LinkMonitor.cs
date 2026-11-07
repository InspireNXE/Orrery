using Avalonia.Threading;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

/// <summary>How a connection is doing, as the tab's mark shows it.</summary>
enum LinkHealth
{
    /// <summary>Not connected, or nothing measured yet.</summary>
    Unknown,
    Good,

    /// <summary>A long round trip, or this computer spending most of its time decoding: worth a look.</summary>
    Slow,
}

/// <summary>One second of a connection: the rates over it, and the round trip at its end.</summary>
readonly record struct LinkSample(double ReceivedPerSecond, double SentPerSecond, double UpdatesPerSecond, double BusyShare, TimeSpan? RoundTrip);

/// <summary>What kind of thing happened, which sets its mark on the Connection card's graph.</summary>
enum LinkEventKind
{
    Connected,

    /// <summary>The connection dropped: a failure, not a session the server or user ended.</summary>
    Lost,

    /// <summary>The server ended the session.</summary>
    Ended,
    Resized,

    /// <summary>Files copied on the remote finished downloading here.</summary>
    Copied,
}

/// <summary>Something that happened to the connection, for the card's Recent list and its graph.</summary>
/// <param name="Value">A figure to go with it, such as how long it took; null when there's none.</param>
sealed record LinkEvent(DateTimeOffset At, LinkEventKind Kind, string Text, string? Value);

/// <summary>
/// Reads a session's <see cref="LinkStats"/> once a second, and counts a remote desktop's screen updates itself, keeping
/// the last five minutes for the Connection card's graph and judging the link for the tab's mark. Also notes what
/// happened along the way (connecting, resizes, drops, downloaded files), carried over when the tab reconnects. Runs
/// on the UI thread while the session is connected.
/// </summary>
sealed class LinkMonitor
{
    /// <summary>How much history the graph shows, in samples of a second.</summary>
    public const int HistoryLength = 300;

    /// <summary>How many events the card keeps.</summary>
    public const int EventsKept = 20;

    /// <summary>How long to wait for the first picture's connect time before noting the connection without it.</summary>
    static readonly TimeSpan ConnectTimeWait = TimeSpan.FromSeconds(30);

    /// <summary>A round trip from here on reads as slow. Typing and the pointer start to feel behind.</summary>
    public static readonly TimeSpan SlowRoundTrip = TimeSpan.FromMilliseconds(150);

    /// <summary>Decoding taking this share of a second means this computer, not the link, is holding things up.</summary>
    public const double BusyShareSlow = 0.8;

    readonly IConnectionSession _session;
    readonly LinkStats? _link;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Queue<LinkSample> _history = new();
    readonly List<LinkEvent> _events = [];
    long _updates, _lastUpdates;
    LinkReading _last;
    long _lastAt;
    int _lastResizes;
    bool _wasConnected, _connectNoted, _reconnected;
    long? _copyStarted;

    public LinkMonitor(IConnectionSession session)
    {
        _session = session;
        _link = (session as ILinkStatsSession)?.Link;
        Started = DateTimeOffset.Now;
        if (session is IFrameBufferSession frames)
        {
            frames.FrameUpdated += (_, _) => Interlocked.Increment(ref _updates);
        }

        if (session is IClipboardSession clipboard)
        {
            clipboard.ClipboardTransferChanged += (_, transfer) => Dispatcher.UIThread.Post(() => FollowTransfer(transfer));
        }

        _timer.Tick += (_, _) => Sample();
        session.StateChanged += (_, _) => Dispatcher.UIThread.Post(FollowState);
        FollowState();
    }

    /// <summary>Whether there's anything to show: a session that keeps stats, or a remote desktop's updates.</summary>
    public static bool Covers(IConnectionSession session) => session is ILinkStatsSession or IFrameBufferSession;

    public IConnectionSession Session => _session;

    /// <summary>The session's traffic and timings, when it keeps them.</summary>
    public LinkStats? Link => _link;

    public bool CountsUpdates => _session is IFrameBufferSession;

    public DateTimeOffset Started { get; }

    /// <summary>When the session last connected.</summary>
    public DateTimeOffset? ConnectedAt { get; private set; }

    public IReadOnlyCollection<LinkSample> History => _history;

    public LinkSample? Latest => _history.Count > 0 ? _history.Last() : null;

    /// <summary>The totals so far.</summary>
    public LinkReading Totals => _last;

    public LinkHealth Health { get; private set; }

    /// <summary>What happened, oldest first, including what the tab's earlier connections noted.</summary>
    public IReadOnlyList<LinkEvent> Events => _events;

    /// <summary>How many times the connection dropped in this tab, counting earlier connections.</summary>
    public int Drops { get; private set; }

    /// <summary>A sample was taken, or the session's state changed. On the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Takes over what the tab's previous connection noted, when a new one replaces it: its events and drops. The
    /// next connect then reads as a reconnect.
    /// </summary>
    public void CarryFrom(LinkMonitor previous)
    {
        _events.InsertRange(0, previous._events);
        Trim();
        Drops += previous.Drops;
        _reconnected = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void FollowState()
    {
        var state = _session.State;
        if (state == SessionState.Connected)
        {
            ConnectedAt ??= DateTimeOffset.Now;
            _wasConnected = true;
            if (!_timer.IsEnabled)
            {
                _last = _link?.Read() ?? default;
                _lastAt = System.Diagnostics.Stopwatch.GetTimestamp();
                _lastUpdates = Interlocked.Read(ref _updates);
                _lastResizes = _link?.Resizes ?? 0;
                _timer.Start();
            }

            NoteConnected();
        }
        else
        {
            _timer.Stop();
            Health = LinkHealth.Unknown;
            if (_wasConnected && state is SessionState.Failed or SessionState.Disconnected)
            {
                _wasConnected = false;
                if (state == SessionState.Failed)
                {
                    Drops++;
                    Note(LinkEventKind.Lost, "Connection lost", null);
                }
                else
                {
                    Note(LinkEventKind.Ended, "Session ended", null);
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Notes the connection once its connect time is known, which for a remote desktop is at the first picture, a
    /// moment after the session says it's connected; without one after a while, notes it anyway.
    /// </summary>
    void NoteConnected()
    {
        if (_connectNoted || ConnectedAt is not { } at)
        {
            return;
        }

        var took = _link?.ConnectTime;
        if (took is null && _link is not null && DateTimeOffset.Now - at < ConnectTimeWait)
        {
            return;
        }

        _connectNoted = true;
        _events.Add(new LinkEvent(at, LinkEventKind.Connected, _reconnected ? "Reconnected" : "Connected", took is { } t ? Milliseconds(t) : null));
        Trim();
    }

    /// <summary>Files copied on the remote downloading here: noted once they're all in, with how fast they came.</summary>
    internal void FollowTransfer(ClipboardTransfer transfer)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _copyStarted ??= now;
        if (!transfer.Ended)
        {
            return;
        }

        var took = System.Diagnostics.Stopwatch.GetElapsedTime(_copyStarted.Value, now);
        _copyStarted = null;
        if (transfer.Total <= 0 || transfer.Done < transfer.Total)
        {
            return;
        }

        string what = transfer.Files == 1 ? "1 file" : $"{transfer.Files} files";
        string speed = took >= TimeSpan.FromMilliseconds(200) ? $" · {Size((long)(transfer.Total / took.TotalSeconds))}/s" : string.Empty;
        Note(LinkEventKind.Copied, $"Copied {what} from the remote", Size(transfer.Total) + speed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Note(LinkEventKind kind, string text, string? value)
    {
        _events.Add(new LinkEvent(DateTimeOffset.Now, kind, text, value));
        Trim();
    }

    void Trim()
    {
        if (_events.Count > EventsKept)
        {
            _events.RemoveRange(0, _events.Count - EventsKept);
        }
    }

    /// <summary>Takes a sample now; the timer calls it every second. Internal for tests.</summary>
    internal void Sample()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = Math.Max(0.001, System.Diagnostics.Stopwatch.GetElapsedTime(_lastAt, now).TotalSeconds);
        var reading = _link?.Read() ?? default;
        long updates = Interlocked.Read(ref _updates);

        var sample = new LinkSample(
            (reading.Received - _last.Received) / seconds,
            (reading.Sent - _last.Sent) / seconds,
            (updates - _lastUpdates) / seconds,
            Math.Clamp((reading.Busy - _last.Busy).TotalSeconds / seconds, 0, 1),
            reading.RoundTrip);
        (_last, _lastAt, _lastUpdates) = (reading, now, updates);

        NoteConnected();
        if (_link is { } link && link.Resizes != _lastResizes)
        {
            _lastResizes = link.Resizes;
            string size = _session is IFrameBufferSession frames && frames.Width > 0 ? $"Resized to {frames.Width} × {frames.Height}" : "Resized";
            Note(LinkEventKind.Resized, size, link.LastResize is { } took ? Milliseconds(took) : null);
        }

        Add(sample);
    }

    /// <summary>Adds a sample as if measured. Internal for tests.</summary>
    internal void Add(LinkSample sample)
    {
        _history.Enqueue(sample);
        while (_history.Count > HistoryLength)
        {
            _history.Dequeue();
        }

        Health = Judge(_history);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Slow when the round trip is long, or decoding has taken most of the time for a few seconds running; one busy
    /// second (the first picture, a resize) doesn't count.
    /// </summary>
    internal static LinkHealth Judge(IReadOnlyCollection<LinkSample> history)
    {
        if (history.Count == 0)
        {
            return LinkHealth.Unknown;
        }

        var latest = history.Last();
        if (latest.RoundTrip >= SlowRoundTrip)
        {
            return LinkHealth.Slow;
        }

        var recent = history.Skip(Math.Max(0, history.Count - 3)).ToList();
        return recent.Count == 3 && recent.All(s => s.BusyShare >= BusyShareSlow) ? LinkHealth.Slow : LinkHealth.Good;
    }

    /// <summary>The verdict at the top of the card, in a word or two.</summary>
    public string Verdict() => Health switch
    {
        LinkHealth.Good => "Good",
        LinkHealth.Slow => "Slow",
        _ => _session.State == SessionState.Connected ? "Measuring" : "Not connected",
    };

    /// <summary>
    /// The steps connecting took, with what they don't cover (an earlier host that redirected, a tunnel) as Other once
    /// it's worth showing. Empty when the session doesn't time its steps.
    /// </summary>
    public IReadOnlyList<ConnectStep> ConnectSteps()
    {
        if (_link is not { ConnectTime: { } total } link || link.ConnectSteps is not { Count: > 0 } steps)
        {
            return [];
        }

        var rest = total - steps.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Time);
        return rest >= TimeSpan.FromMilliseconds(100) && rest.TotalMilliseconds >= total.TotalMilliseconds * 0.03
            ? [.. steps, new ConnectStep("Other", rest)]
            : steps;
    }

    /// <summary>The drops line: none, or how many and when the last was.</summary>
    public string DropsText()
    {
        if (Drops == 0)
        {
            return "none";
        }

        var last = _events.LastOrDefault(e => e.Kind == LinkEventKind.Lost);
        return last is null ? $"{Drops}" : $"{Drops}, last {Ago(DateTimeOffset.Now - last.At)}";
    }

    /// <summary>How long ago, as the Recent list says it.</summary>
    public static string Ago(TimeSpan time) => time.TotalMinutes < 1 ? "just now" : Duration(time) + " ago";

    /// <summary>What to make of the numbers, in plain words; empty while all is well.</summary>
    public IReadOnlyList<string> Hints()
    {
        var hints = new List<string>();
        if (Latest is not { } latest)
        {
            return hints;
        }

        bool desktop = CountsUpdates;
        if (latest.RoundTrip is { } rtt && rtt >= SlowRoundTrip)
        {
            hints.Add($"The round trip is {Milliseconds(rtt)}. That's the network, not Orrery: "
                + (desktop ? "the Fastest display quality sends less over it." : "typing will echo late, whatever Orrery does."));
        }

        if (Health == LinkHealth.Slow && latest.BusyShare >= BusyShareSlow)
        {
            hints.Add("Decoding the picture takes most of this computer's time, so it's the slow part rather than the network. "
                + "A smaller desktop or the Fastest display quality gives it less to do.");
        }

        return hints;
    }

    /// <summary>A rate in bytes a second as bits a second, the way network speeds are given.</summary>
    public static string Rate(double bytesPerSecond)
    {
        double bits = bytesPerSecond * 8;
        return bits switch
        {
            >= 1_000_000_000 => $"{bits / 1_000_000_000:0.0} Gbit/s",
            >= 1_000_000 => $"{bits / 1_000_000:0.0} Mbit/s",
            >= 1_000 => $"{bits / 1_000:0} kbit/s",
            _ => $"{bits:0} bit/s",
        };
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes",
    };

    public static string Milliseconds(TimeSpan time) => time.TotalMilliseconds >= 10_000
        ? $"{time.TotalSeconds:0} s"
        : time.TotalMilliseconds >= 1_000 ? $"{time.TotalSeconds:0.0} s" : $"{Math.Max(1, Math.Round(time.TotalMilliseconds)):0} ms";

    public static string Duration(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours} h {time.Minutes} min"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min" : "under a minute";

    /// <summary>The numbers in a line, for the tab's tooltip.</summary>
    public string? Summary()
    {
        if (Latest is not { } latest)
        {
            return null;
        }

        var parts = new List<string>();
        if (CountsUpdates)
        {
            parts.Add($"{latest.UpdatesPerSecond:0} updates/s");
        }

        if (_link is not null)
        {
            parts.Add(Rate(latest.ReceivedPerSecond) + " in");
        }

        if (latest.RoundTrip is { } rtt)
        {
            parts.Add(Milliseconds(rtt) + " round trip");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

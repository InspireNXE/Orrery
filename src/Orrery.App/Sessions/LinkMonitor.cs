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

/// <summary>
/// Reads a session's <see cref="LinkStats"/> once a second, and counts a remote desktop's screen updates itself, keeping
/// the last minute for the Connection card's graphs and judging the link for the tab's mark. Runs on the UI thread
/// while the session is connected.
/// </summary>
sealed class LinkMonitor
{
    /// <summary>How much history the graphs show, in samples of a second.</summary>
    public const int HistoryLength = 60;

    /// <summary>A round trip from here on reads as slow. Typing and the pointer start to feel behind.</summary>
    public static readonly TimeSpan SlowRoundTrip = TimeSpan.FromMilliseconds(150);

    /// <summary>Decoding taking this share of a second means this computer, not the link, is holding things up.</summary>
    public const double BusyShareSlow = 0.8;

    readonly IConnectionSession _session;
    readonly LinkStats? _link;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Queue<LinkSample> _history = new();
    long _updates, _lastUpdates;
    LinkReading _last;
    long _lastAt;

    public LinkMonitor(IConnectionSession session)
    {
        _session = session;
        _link = (session as ILinkStatsSession)?.Link;
        Started = DateTimeOffset.Now;
        if (session is IFrameBufferSession frames)
        {
            frames.FrameUpdated += (_, _) => Interlocked.Increment(ref _updates);
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

    /// <summary>A sample was taken, or the session's state changed. On the UI thread.</summary>
    public event EventHandler? Changed;

    void FollowState()
    {
        if (_session.State == SessionState.Connected)
        {
            ConnectedAt ??= DateTimeOffset.Now;
            if (!_timer.IsEnabled)
            {
                _last = _link?.Read() ?? default;
                _lastAt = System.Diagnostics.Stopwatch.GetTimestamp();
                _lastUpdates = Interlocked.Read(ref _updates);
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
            Health = LinkHealth.Unknown;
        }

        Changed?.Invoke(this, EventArgs.Empty);
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

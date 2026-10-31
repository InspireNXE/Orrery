using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orrery.App.Sessions;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;

namespace Orrery.App.Logging;

/// <summary>
/// Writes a session's life to the log under a short id of its own (<c>rdp-3f2a</c>), so one session's lines can be
/// picked out: opening, connected and how long it took, and how it ended.
/// </summary>
sealed class SessionLogger
{
    static readonly ILogger Logger = Log.For("Session");

    // Sessions not yet ended, so the ones still open when Orrery closes get their last line too. UI thread only.
    static readonly HashSet<SessionLogger> Open = [];

    readonly SessionTab _tab;
    readonly Stopwatch _opened = Stopwatch.StartNew();
    TimeSpan? _connectedAt;
    SessionState _logged = SessionState.Connecting;

    SessionLogger(SessionTab tab)
    {
        _tab = tab;
        Id = $"{(tab.Origin.Files ? "sftp" : tab.Origin.Protocol.ToString().ToLowerInvariant())}-{Random.Shared.Next(0x10000):x4}";
    }

    public string Id { get; }

    public static SessionLogger Attach(SessionTab tab)
    {
        var logger = new SessionLogger(tab);
        var target = tab.Session.Target;
        Logger.LogInformation("[{Session}] Opening \"{Title}\" on {Host}:{Port}{Attempt}", logger.Id, tab.Origin.Title, target.Host, target.Port,
            tab.ReconnectAttempt > 0 ? $" (reconnect attempt {tab.ReconnectAttempt})" : string.Empty);
        tab.StateChanged += (_, _) => logger.OnStateChanged();
        Open.Add(logger);
        return logger;
    }

    /// <summary>Writes the end of every session still open; Orrery is closing and their own ends won't be seen.</summary>
    public static void CloseAll()
    {
        foreach (var logger in Open)
        {
            var elapsed = logger._opened.Elapsed;
            Logger.LogInformation("[{Session}] Closed with Orrery after {Duration}", logger.Id,
                Duration(logger._connectedAt is { } since ? elapsed - since : elapsed));
        }

        Open.Clear();
    }

    void OnStateChanged()
    {
        var state = _tab.Session.State;
        if (state == _logged)
        {
            return;
        }

        _logged = state;
        if (state is SessionState.Disconnected or SessionState.Failed)
        {
            Open.Remove(this);
        }

        var elapsed = _opened.Elapsed;
        switch (state)
        {
            case SessionState.Connected:
                _connectedAt = elapsed;
                Logger.LogInformation("[{Session}] Connected in {Seconds:0.0} s", Id, elapsed.TotalSeconds);
                break;

            case SessionState.Disconnected:
                Logger.LogInformation("[{Session}] {How} after {Duration}{Reason}", Id, _tab.EndedByUser ? "Closed" : "Disconnected",
                    Duration(_connectedAt is { } since ? elapsed - since : elapsed), Reason());
                break;

            case SessionState.Failed when _connectedAt is { } droppedFrom:
                Logger.LogWarning("[{Session}] Dropped after {Duration}{Reason}", Id, Duration(elapsed - droppedFrom), Reason());
                break;

            case SessionState.Failed:
                Logger.LogWarning("[{Session}] Couldn't connect after {Seconds:0.0} s{Reason}", Id, elapsed.TotalSeconds, Reason());
                break;
        }
    }

    string Reason() => string.IsNullOrWhiteSpace(_tab.LastMessage) ? string.Empty : ": " + _tab.LastMessage;

    /// <summary>"42 s", "12 min 5 s" or "3 h 20 min".</summary>
    internal static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => $"{span.TotalSeconds:0} s",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min {span.Seconds} s",
        _ => $"{(int)span.TotalHours} h {span.Minutes} min",
    };
}

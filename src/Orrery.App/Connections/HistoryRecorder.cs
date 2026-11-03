using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;

namespace Orrery.App.Connections;

/// <summary>
/// Notes in <see cref="ConnectionHistory"/> when a tab's session gets in, which for SSH, RDP and VNC is after the
/// login has succeeded, and when it never does. A session that drops after connecting isn't a failed attempt.
/// Only saved connections still in the tree are noted, not quick connects or ones deleted while open.
/// </summary>
sealed class HistoryRecorder
{
    readonly SessionTab _tab;
    readonly ConnectionTree _tree;
    readonly ConnectionHistory _history;
    readonly Func<DateTimeOffset> _now;
    bool _connectedNoted, _failedNoted;

    HistoryRecorder(SessionTab tab, ConnectionTree tree, ConnectionHistory history, Func<DateTimeOffset> now)
    {
        (_tab, _tree, _history, _now) = (tab, tree, history, now);
    }

    public static void Attach(SessionTab tab, ConnectionTree tree, ConnectionHistory history, Func<DateTimeOffset>? now = null)
    {
        var recorder = new HistoryRecorder(tab, tree, history, now ?? (() => DateTimeOffset.Now));
        tab.StateChanged += (_, _) => recorder.OnStateChanged();
    }

    void OnStateChanged()
    {
        if (_tab.Origin.EntryId is not { } id || _tree.Find(id) is not ConnectionEntry || _tab.Origin.ManagementPage)
        {
            return;
        }

        switch (_tab.Session.State)
        {
            case SessionState.Connected when !_connectedNoted:
                _connectedNoted = true;
                _history.RecordConnected(id, _now());
                break;

            case SessionState.Failed when !_tab.HasConnected && !_failedNoted:
                _failedNoted = true;
                _history.RecordFailed(id, _now());
                break;
        }
    }
}

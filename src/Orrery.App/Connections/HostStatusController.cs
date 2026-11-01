using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Network;

namespace Orrery.App.Connections;

/// <summary>
/// Puts the up/down dots on the connection tree. A short timer asks the monitor to check the connections that
/// are on screen (in expanded folders, with the connections panel showing); the monitor only probes those it
/// hasn't heard from in <see cref="CheckInterval"/>. Turned off, it sends nothing and the rows show no dot.
/// </summary>
sealed class HostStatusController : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>How often to look for rows that came into view or are due; cheap, since the monitor decides what to probe.</summary>
    static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    static readonly ILogger Logger = Log.For("HostStatus");

    readonly Func<IEnumerable<NodeViewModel>> _rows;
    readonly Func<bool> _isShown;
    readonly HostStatusMonitor _monitor;
    readonly DispatcherTimer _timer;
    CancellationTokenSource? _running;
    bool _enabled;
    bool _applyQueued;

    /// <param name="rows">The tree's top-level rows.</param>
    /// <param name="isShown">Whether the tree is on screen; nothing is probed while it isn't.</param>
    public HostStatusController(Func<IEnumerable<NodeViewModel>> rows, Func<bool> isShown, IHostProbe? probe = null)
    {
        _rows = rows;
        _isShown = isShown;
        _monitor = new HostStatusMonitor(probe ?? new TcpHostProbe());
        _monitor.Updated += (_, _) => QueueApply();
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Check();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Logger.LogInformation("Host up/down checks turned {State}", value ? "on" : "off");
            if (value)
            {
                _running = new CancellationTokenSource();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _running?.Cancel();
                _running?.Dispose();
                _running = null;
                _monitor.Clear();
            }

            Refresh();
        }
    }

    /// <summary>The rows were rebuilt or something else changed: redraw the dots and check what's due.</summary>
    public void Refresh()
    {
        Apply();
        Check();
    }

    /// <summary>Sets every connection row's dot from what the monitor knows.</summary>
    public void Apply()
    {
        foreach (var row in All(_rows()))
        {
            if (row.Node is not ConnectionEntry entry)
            {
                continue;
            }

            if (!_enabled)
            {
                row.SetReachability(null, null);
            }
            else if (ProbeTarget.For(entry, out var why) is not { } target)
            {
                row.SetReachability(HostState.Unknown, $"Not checked: {why}.");
            }
            else if (_monitor.Get(target) is { } check)
            {
                var time = check.At.ToLocalTime().ToString("T");
                row.SetReachability(check.State, check.State == HostState.Up
                    ? $"Up: port {target.Port} answered. Checked {time}."
                    : $"Down: nothing answered on port {target.Port}. Checked {time}.");
            }
            else
            {
                row.SetReachability(HostState.Unknown, "Not checked: waiting for the first check.");
            }
        }
    }

    void Check()
    {
        if (!_enabled || _running is not { } running || !_isShown())
        {
            return;
        }

        var targets = Visible(_rows()).Select(r => r.Node).OfType<ConnectionEntry>()
            .Select(e => ProbeTarget.For(e, out _)).OfType<ProbeTarget>().ToList();
        if (targets.Count > 0)
        {
            _ = RunAsync(targets, running.Token);
        }
    }

    async Task RunAsync(List<ProbeTarget> targets, CancellationToken cancellationToken)
    {
        try
        {
            await _monitor.CheckAsync(targets, CheckInterval, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // A probe failing must never reach the user or stop the app; the dot just stays as it was.
            Logger.LogWarning(e, "Checking which hosts are up failed");
        }
    }

    /// <summary>Probes finish on background threads; the dots are redrawn once on the UI thread for a burst of them.</summary>
    void QueueApply()
    {
        if (Interlocked.Exchange(ref _applyQueued, true))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _applyQueued = false;
            Apply();
        });
    }

    /// <summary>Every row, in tree order, whether or not its folder is open.</summary>
    static IEnumerable<NodeViewModel> All(IEnumerable<NodeViewModel> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            foreach (var child in All(row.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>The rows you can see: those whose folders (and hosts) above them are all open.</summary>
    static IEnumerable<NodeViewModel> Visible(IEnumerable<NodeViewModel> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            if (row.IsExpanded)
            {
                foreach (var child in Visible(row.Children))
                {
                    yield return child;
                }
            }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
    }
}

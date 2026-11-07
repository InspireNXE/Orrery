namespace Orrery.Core.Network;

/// <summary>
/// Follows a host after its session ended: whether it stops answering on the session's port, and when it answers
/// again, so a computer that restarts or goes offline is reconnected to once it's back instead of after a fixed count
/// of tries. It probes the port the session used, the way the host checks do over TCP.
/// </summary>
/// <param name="delay">Waits between probes; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> if null. For tests.</param>
/// <param name="now">The clock; the system's if null. For tests.</param>
public sealed class HostReturnWatch(IHostProbe probe, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(3);

    /// <summary>How long after a session ends its host has to stop answering for that to count.</summary>
    public static readonly TimeSpan DownWithin = TimeSpan.FromSeconds(60);

    /// <summary>The longest wait for a host to answer again: a computer installing updates can take a while.</summary>
    public static readonly TimeSpan WaitAtMost = TimeSpan.FromMinutes(30);

    /// <summary>
    /// No answers in a row that count as the host having stopped answering, so one probe lost on a busy network
    /// doesn't make a logoff look like the computer going away.
    /// </summary>
    public const int DownAnswersNeeded = 2;

    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// True once <paramref name="host"/> stops answering on <paramref name="port"/> within <see cref="DownWithin"/>;
    /// false if it keeps answering, or <paramref name="keepGoing"/> says to stop.
    /// </summary>
    public async Task<bool> GoesDownAsync(string host, int port, Func<bool> keepGoing, CancellationToken token)
    {
        var until = _now() + DownWithin;
        int down = 0;
        while (keepGoing())
        {
            down = (await probe.ProbeAsync(host, port, token)).State == HostState.Down ? down + 1 : 0;
            if (down >= DownAnswersNeeded)
            {
                return true;
            }

            if (_now() >= until)
            {
                return false;
            }

            await _delay(CheckEvery, token);
        }

        return false;
    }

    /// <summary>
    /// True once <paramref name="host"/> answers on <paramref name="port"/> again; false after
    /// <see cref="WaitAtMost"/>, or when <paramref name="keepGoing"/> says to stop.
    /// </summary>
    public async Task<bool> ComesBackAsync(string host, int port, Func<bool> keepGoing, CancellationToken token)
    {
        var until = _now() + WaitAtMost;
        while (keepGoing())
        {
            if ((await probe.ProbeAsync(host, port, token)).State == HostState.Up)
            {
                return true;
            }

            if (_now() >= until)
            {
                return false;
            }

            await _delay(CheckEvery, token);
        }

        return false;
    }
}

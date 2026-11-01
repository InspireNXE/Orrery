using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;

namespace Orrery.Core.Network;

public enum HostState
{
    Unknown,
    Up,
    Down,
}

/// <summary>How a host is checked.</summary>
public enum HostCheckMethod
{
    /// <summary>A TCP connection to the connection's own port, closed as soon as it opens.</summary>
    Tcp,

    /// <summary>One ICMP echo to the host, whatever its port.</summary>
    Ping,
}

/// <summary>What to probe for one connection: its host and port as it would be connected to.</summary>
public sealed record ProbeTarget(Guid Id, string Host, int Port)
{
    /// <summary>
    /// The target for <paramref name="entry"/>, or null with <paramref name="whyNot"/> saying why it can't be checked.
    /// A connection reached through a jump host isn't probed: the jump host's reachability would say nothing about it,
    /// and probing it directly would be a lie (or a leak, when the route exists to avoid reaching it from here).
    /// </summary>
    public static ProbeTarget? For(ConnectionEntry entry, out string? whyNot)
    {
        whyNot = null;
        var resolved = SettingsResolver.Resolve(entry);
        if (resolved.JumpHostId.Value is not null)
        {
            whyNot = "behind a jump host, so it can't be checked from here";
            return null;
        }

        if (!HostAddress.TryParse(entry.Host, resolved.Port.Value, out var host, out var port))
        {
            whyNot = string.IsNullOrWhiteSpace(entry.Host) ? "no host is set" : "the host isn't a name or address Orrery can check";
            return null;
        }

        return new ProbeTarget(entry.Id, host, port);
    }
}

/// <summary>
/// The answer to one probe, how it was made and when it came. <see cref="HostState.Unknown"/> means the probe
/// couldn't be made at all, such as when this computer doesn't allow ping, and <see cref="WhyNot"/> says why.
/// </summary>
public sealed record HostCheck(ProbeTarget Target, HostState State, DateTimeOffset At, HostCheckMethod Method = HostCheckMethod.Tcp, string? WhyNot = null);

/// <summary>What one probe found: up, down, or <see cref="HostState.Unknown"/> with the reason it couldn't check.</summary>
public sealed record ProbeAnswer(HostState State, string? WhyNot = null)
{
    public static readonly ProbeAnswer Up = new(HostState.Up);
    public static readonly ProbeAnswer Down = new(HostState.Down);
}

public interface IHostProbe
{
    HostCheckMethod Method { get; }

    /// <summary>
    /// Which targets one probe answers for: those with the same key (ignoring case) are probed once a round and
    /// share the answer.
    /// </summary>
    string KeyFor(string host, int port);

    /// <summary>Whether <paramref name="host"/> (at <paramref name="port"/>, if the method uses one) answered. Only cancellation throws.</summary>
    Task<ProbeAnswer> ProbeAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>A plain TCP connect: up if it completes within the timeout, down if it fails or runs out of time. Nothing is sent.</summary>
public sealed class TcpHostProbe(TimeSpan? timeout = null) : IHostProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public HostCheckMethod Method => HostCheckMethod.Tcp;

    public string KeyFor(string host, int port) => $"{host}:{port}";

    public async Task<ProbeAnswer> ProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, limit.Token);
            return ProbeAnswer.Up;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeAnswer.Down;
        }
        catch (Exception e) when (e is SocketException or ArgumentException or IOException or ObjectDisposedException)
        {
            return ProbeAnswer.Down;
        }
    }
}

/// <summary>
/// One ICMP echo, sent with <see cref="Ping"/>: up if the host replies within the timeout, down if it doesn't or a
/// router says it can't be reached. Where this computer won't send one (on Linux .NET needs raw sockets or a
/// working <c>ping</c> program), the answer is <see cref="HostState.Unknown"/>, since that says nothing about the host.
/// </summary>
public sealed class PingHostProbe(TimeSpan? timeout = null) : IHostProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    internal const string NotAllowed = "this computer doesn't let Orrery ping";

    static readonly ILogger Logger = Log.For("HostStatus");
    static int _warned;

    readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public HostCheckMethod Method => HostCheckMethod.Ping;

    /// <summary>The host alone: connections to different ports of one host share a ping.</summary>
    public string KeyFor(string host, int port) => host;

    public async Task<ProbeAnswer> ProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Where .NET runs the ping program, its timeout is rounded up to whole seconds; this one stops a hung one.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout + TimeSpan.FromSeconds(1));
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, _timeout, null, null, limit.Token);
            return Answer(reply.Status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeAnswer.Down;
        }
        catch (Exception e) when (IsUnknownName(e))
        {
            // The name didn't resolve: down, as a TCP check would say.
            return ProbeAnswer.Down;
        }
        catch (Exception e) when (e is PingException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return CantPing(e);
        }
        catch (Exception e) when (e is ArgumentException or SocketException or ObjectDisposedException)
        {
            return ProbeAnswer.Down;
        }
    }

    /// <summary>A reply, or a router saying the host can't be reached, is an answer; anything else means the echo never left.</summary>
    internal static ProbeAnswer Answer(IPStatus status) => status switch
    {
        IPStatus.Success => ProbeAnswer.Up,
        IPStatus.TimedOut or IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationUnreachable
            or IPStatus.DestinationPortUnreachable or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationScopeMismatch
            or IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded or IPStatus.BadRoute or IPStatus.SourceQuench
            => ProbeAnswer.Down,
        _ => new ProbeAnswer(HostState.Unknown, $"the ping couldn't be sent ({status})"),
    };

    /// <summary>Ping failing on this computer rather than at the host: no permission, no ping program, no support.</summary>
    internal static ProbeAnswer CantPing(Exception e)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            Logger.LogWarning("Ping isn't working here, so hosts show as not checked: {Message}", (e.InnerException ?? e).Message);
        }

        return new ProbeAnswer(HostState.Unknown, NotAllowed);
    }

    static bool IsUnknownName(Exception e) =>
        (e as SocketException ?? e.InnerException as SocketException) is { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain };
}

public static class HostProbes
{
    /// <summary>The probe for <paramref name="method"/>, with its default timeout.</summary>
    public static IHostProbe For(HostCheckMethod method) => method == HostCheckMethod.Ping ? new PingHostProbe() : new TcpHostProbe();
}

/// <summary>
/// Keeps the last answer for each connection and probes the ones that are due, a few at a time. It never schedules
/// itself: the app calls <see cref="CheckAsync"/> with the connections that are on screen, as often as it likes, and
/// only those with no answer, an old one or one made by another method are probed. Targets that share a
/// <see cref="IHostProbe.KeyFor"/> key, such as several connections to one host when pinging, get one probe between them.
/// </summary>
public sealed class HostStatusMonitor(IHostProbe probe, int maxConcurrency = HostStatusMonitor.DefaultConcurrency, TimeProvider? time = null)
{
    public const int DefaultConcurrency = 8;

    /// <summary>The most probes one call makes, so an import of thousands of connections can't turn into thousands of probes at once.</summary>
    public const int MaxTargetsPerCall = 200;

    static readonly ILogger Logger = Log.For("HostStatus");

    readonly ConcurrentDictionary<Guid, HostCheck> _results = new();
    readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _slots = new(maxConcurrency);
    readonly TimeProvider _time = time ?? TimeProvider.System;
    IHostProbe _probe = probe;

    /// <summary>
    /// How hosts are checked. A change applies from the next <see cref="CheckAsync"/>, which probes again every
    /// target whose answer was made the old way; those answers still show until then.
    /// </summary>
    public IHostProbe Probe
    {
        get => Volatile.Read(ref _probe);
        set => Volatile.Write(ref _probe, value);
    }

    /// <summary>A probe finished and changed what <see cref="Get"/> returns. Raised on a background thread.</summary>
    public event EventHandler<Guid>? Updated;

    /// <summary>The latest answer for <paramref name="target"/>, or null if there is none yet or the answer was for another host or port.</summary>
    public HostCheck? Get(ProbeTarget target) =>
        _results.TryGetValue(target.Id, out var check) && check.Target == target ? check : null;

    /// <summary>Forgets every answer, such as when checks are turned off.</summary>
    public void Clear() => _results.Clear();

    /// <summary>
    /// Probes each target with no answer, one at least <paramref name="maxAge"/> old, or one made by another method,
    /// and returns when they're done.
    /// </summary>
    public async Task CheckAsync(IEnumerable<ProbeTarget> targets, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var probe = Probe;
        var now = _time.GetUtcNow();
        var due = targets
            .Where(t => Get(t) is not { } check || check.Method != probe.Method || now - check.At >= maxAge)
            .DistinctBy(t => t.Id)
            .GroupBy(t => probe.KeyFor(t.Host, t.Port), StringComparer.OrdinalIgnoreCase)
            .Take(MaxTargetsPerCall)
            .ToList();
        await Task.WhenAll(due.Select(group => CheckOneAsync(probe, group.Key, [.. group], cancellationToken)));
    }

    /// <summary>One probe, whose answer goes to every target in <paramref name="targets"/>.</summary>
    async Task CheckOneAsync(IHostProbe probe, string key, List<ProbeTarget> targets, CancellationToken cancellationToken)
    {
        // A slow probe from an earlier call is still running; don't start a second.
        if (!_running.TryAdd(key, true))
        {
            return;
        }

        try
        {
            await _slots.WaitAsync(cancellationToken);
            try
            {
                var answer = await probe.ProbeAsync(targets[0].Host, targets[0].Port, cancellationToken);
                var at = _time.GetUtcNow();
                foreach (var target in targets)
                {
                    var previous = _results.TryGetValue(target.Id, out var old) && old.Target == target ? old.State : HostState.Unknown;
                    _results[target.Id] = new HostCheck(target, answer.State, at, probe.Method, answer.WhyNot);
                    if (previous != answer.State)
                    {
                        Logger.LogDebug("{Host}:{Port} is now {State} by {Method}", target.Host, target.Port, answer.State, probe.Method);
                    }

                    Updated?.Invoke(this, target.Id);
                }
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Checks were turned off, or the window closed, part-way through.
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }
}

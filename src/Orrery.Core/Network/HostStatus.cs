using System.Collections.Concurrent;
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

/// <summary>The answer to one probe and when it came.</summary>
public sealed record HostCheck(ProbeTarget Target, HostState State, DateTimeOffset At);

public interface IHostProbe
{
    /// <summary>Whether something accepted a connection at <paramref name="host"/>:<paramref name="port"/>. Only cancellation throws.</summary>
    Task<bool> IsUpAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>A plain TCP connect: up if it completes within the timeout, down if it fails or runs out of time. Nothing is sent.</summary>
public sealed class TcpHostProbe(TimeSpan? timeout = null) : IHostProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<bool> IsUpAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, limit.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e) when (e is SocketException or ArgumentException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }
}

/// <summary>
/// Keeps the last answer for each connection and probes the ones that are due, a few at a time. It never schedules
/// itself: the app calls <see cref="CheckAsync"/> with the connections that are on screen, as often as it likes, and
/// only those with no answer or an old one are probed.
/// </summary>
public sealed class HostStatusMonitor(IHostProbe probe, int maxConcurrency = HostStatusMonitor.DefaultConcurrency, TimeProvider? time = null)
{
    public const int DefaultConcurrency = 8;

    /// <summary>The most one call probes, so an import of thousands of connections can't turn into thousands of probes at once.</summary>
    public const int MaxTargetsPerCall = 200;

    static readonly ILogger Logger = Log.For("HostStatus");

    readonly ConcurrentDictionary<Guid, HostCheck> _results = new();
    readonly ConcurrentDictionary<Guid, bool> _running = new();
    readonly SemaphoreSlim _slots = new(maxConcurrency);
    readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>A probe finished and changed what <see cref="Get"/> returns. Raised on a background thread.</summary>
    public event EventHandler<Guid>? Updated;

    /// <summary>The latest answer for <paramref name="target"/>, or null if there is none yet or the answer was for another host or port.</summary>
    public HostCheck? Get(ProbeTarget target) =>
        _results.TryGetValue(target.Id, out var check) && check.Target == target ? check : null;

    /// <summary>Forgets every answer, such as when checks are turned off.</summary>
    public void Clear() => _results.Clear();

    /// <summary>Probes each target with no answer, or one at least <paramref name="maxAge"/> old, and returns when they're done.</summary>
    public async Task CheckAsync(IEnumerable<ProbeTarget> targets, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var due = targets
            .Where(t => Get(t) is not { } check || now - check.At >= maxAge)
            .DistinctBy(t => t.Id)
            .Take(MaxTargetsPerCall)
            .ToList();
        await Task.WhenAll(due.Select(t => CheckOneAsync(t, cancellationToken)));
    }

    async Task CheckOneAsync(ProbeTarget target, CancellationToken cancellationToken)
    {
        // A slow probe from an earlier call is still running; don't start a second.
        if (!_running.TryAdd(target.Id, true))
        {
            return;
        }

        try
        {
            await _slots.WaitAsync(cancellationToken);
            try
            {
                bool up = await probe.IsUpAsync(target.Host, target.Port, cancellationToken);
                var state = up ? HostState.Up : HostState.Down;
                var previous = _results.TryGetValue(target.Id, out var old) && old.Target == target ? old.State : HostState.Unknown;
                _results[target.Id] = new HostCheck(target, state, _time.GetUtcNow());
                if (previous != state)
                {
                    Logger.LogDebug("{Host}:{Port} is now {State}", target.Host, target.Port, state);
                }

                Updated?.Invoke(this, target.Id);
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
            _running.TryRemove(target.Id, out _);
        }
    }
}

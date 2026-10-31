using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Orrery.Protocols.Ssh;

/// <summary>
/// A way through one or more SSH jump hosts (ssh -J) to any host and port: RDP, VNC and Telnet sessions use it as
/// their route, and SSH sessions for their own jumps. Each jump host is reached through a loopback forward on the
/// one before it and its host key is checked against its own name; the last one forwards a loopback port on this
/// computer to the destination. Disposing closes them all, innermost first.
/// </summary>
public sealed class SshTunnel(IReadOnlyList<SshHop> hops, HostKeyValidator hostKeys) : ISessionRoute
{
    readonly object _lock = new();
    readonly List<SshClient> _clients = [];
    bool _closed;

    public IReadOnlyList<SshHop> Hops { get; } = hops.Count > 0 ? hops : throw new ArgumentException("A tunnel needs at least one jump host.", nameof(hops));

    public async Task<(string Host, int Port)> OpenAsync(string host, int port, CancellationToken token)
    {
        // SSH.NET connects synchronously, and disposing a client doesn't interrupt a handshake already under way,
        // which then only ends at the connect timeout. So a cancelled open returns at once and leaves that to finish
        // in the background: the tunnel is closed by then, so whatever it connects is disposed straight away.
        using var cancel = token.Register(Dispose);
        var open = Task.Run(() => Open(host, port), CancellationToken.None);
        _ = open.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        var end = await open.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        return end;
    }

    /// <summary>
    /// Connects every hop and returns a loopback address and port that reach <paramref name="host"/>:<paramref name="port"/>.
    /// Throws <see cref="RouteException"/> naming the hop that failed, or <see cref="OperationCanceledException"/>
    /// when the tunnel was disposed meanwhile.
    /// </summary>
    internal (string Host, int Port) Open(string host, int port)
    {
        SshClient? through = null;
        foreach (var hop in Hops)
        {
            var (hopHost, hopPort) = through is null ? (hop.Target.Host, hop.Target.Port) : Forward(through, hop.Target.Host, hop.Target.Port);
            through = Connect(hop, hopHost, hopPort);
        }

        return Forward(through!, host, port);
    }

    SshClient Connect(SshHop hop, string host, int port)
    {
        string where = $"Jump host {hop.Target.Host}: ";
        HostKeyCheck? hostKey = null;
        try
        {
            var client = new SshClient(SshSession.ConnectionInfoFor(host, port, hop.Login, hop.AllowLegacy)) { KeepAliveInterval = SshSession.KeepAlive };
            hostKey = HostKeyCheck.Attach(client, hostKeys, hop.Target);
            // A hop that drops later breaks the connections through it, which their sessions report.
            client.ErrorOccurred += (_, e) => Log.For("JumpHost").LogWarning("Jump host {Host}: {Message}", hop.Target.Host, e.Exception.Message);

            lock (_lock)
            {
                if (_closed)
                {
                    client.Dispose();
                    throw new OperationCanceledException();
                }

                _clients.Add(client);
            }

            hostKey.Connect(client);
            return client;
        }
        catch (Exception) when (IsClosed)
        {
            throw new OperationCanceledException();
        }
        catch (SshConnectionException e) when (hostKey?.Rejected == true)
        {
            throw new RouteException($"The host key of jump host {hop.Target.Host} was not trusted.", e);
        }
        catch (SshAuthenticationException e)
        {
            throw new RouteException(where + SshSession.Refusal(e, hop.Login), e);
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            Log.For("JumpHost").LogError(e, "SSH connection to jump host {Host} failed", hop.Target.Host);
            throw new RouteException(where + SshSession.Describe(e, hop.AllowLegacy), e);
        }
    }

    bool IsClosed
    {
        get
        {
            lock (_lock) return _closed;
        }
    }

    /// <summary>A loopback port on this computer that reaches <paramref name="host"/>:<paramref name="port"/> from <paramref name="through"/>.</summary>
    internal static (string Host, int Port) Forward(SshClient through, string host, int port)
    {
        var forward = new ForwardedPortLocal(PortForward.Loopback, 0, host, (uint)port);
        through.AddForwardedPort(forward);
        forward.Start();
        return (PortForward.Loopback, (int)forward.BoundPort);
    }

    public void Dispose()
    {
        SshClient[] clients;
        lock (_lock)
        {
            _closed = true;
            clients = [.. _clients];
            _clients.Clear();
        }

        // Innermost first: each one runs through the one before it.
        foreach (var client in clients.Reverse())
        {
            try
            {
                client.Dispose();
            }
            catch (Exception e) when (e is SshException or ObjectDisposedException or IOException)
            {
                // Already gone.
            }
        }
    }
}

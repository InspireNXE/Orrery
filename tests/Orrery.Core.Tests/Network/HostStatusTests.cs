using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Orrery.Core.Connections;
using Orrery.Core.Network;
using Xunit;

namespace Orrery.Core.Tests.Network;

public class HostStatusTests
{
    sealed class FakeProbe(Func<string, int, bool>? answer = null, HostCheckMethod method = HostCheckMethod.Tcp) : IHostProbe
    {
        int _running, _peak;
        readonly List<string> _calls = [];

        public TaskCompletionSource? Gate { get; set; }
        public int Peak => _peak;
        public int Calls => _calls.Count;
        public IReadOnlyList<string> Probed => _calls;

        public HostCheckMethod Method => method;

        public string KeyFor(string host, int port) => method == HostCheckMethod.Ping ? host : $"{host}:{port}";

        public async Task<ProbeAnswer> ProbeAsync(string host, int port, CancellationToken cancellationToken)
        {
            lock (_calls) _calls.Add($"{host}:{port}");
            int now = Interlocked.Increment(ref _running);
            int peak;
            while (now > (peak = _peak))
            {
                Interlocked.CompareExchange(ref _peak, now, peak);
            }

            try
            {
                if (Gate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await Task.Delay(20, cancellationToken);
                }

                return answer?.Invoke(host, port) ?? true ? ProbeAnswer.Up : ProbeAnswer.Down;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    sealed class ManualTime : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static ProbeTarget Target(string host = "a.example", int port = 22) => new(Guid.NewGuid(), host, port);

    // ---- Which connections are checked ----

    [Fact]
    public void A_connection_is_checked_at_its_host_and_the_protocols_default_port()
    {
        var tree = ConnectionTree.Empty();
        var rdp = new ConnectionEntry { Name = "r", Host = "rdp.example" };
        var ssh = new ConnectionEntry { Name = "s", Host = "ssh.example", Settings = { Protocol = Protocol.Ssh } };
        var custom = new ConnectionEntry { Name = "c", Host = "web.example:8443", Settings = { Protocol = Protocol.Vnc } };
        var ported = new ConnectionEntry { Name = "p", Host = "[::1]", Settings = { Protocol = Protocol.Telnet, Port = 2323 } };
        foreach (var e in new[] { rdp, ssh, custom, ported }) tree.Add(e, tree.Root);

        Assert.Equal(("rdp.example", 3389), Pair(rdp));
        Assert.Equal(("ssh.example", 22), Pair(ssh));
        Assert.Equal(("web.example", 8443), Pair(custom));
        Assert.Equal(("::1", 2323), Pair(ported));

        static (string, int) Pair(ConnectionEntry e)
        {
            var t = ProbeTarget.For(e, out _)!;
            return (t.Host, t.Port);
        }
    }

    [Fact]
    public void A_connection_behind_a_jump_host_is_not_checked_and_says_why()
    {
        var tree = ConnectionTree.Empty();
        var bastion = new ConnectionEntry { Name = "bastion", Host = "bastion.example", Settings = { Protocol = Protocol.Ssh } };
        var folder = new ConnectionFolder { Name = "inside", Settings = { JumpHostId = bastion.Id } };
        var db = new ConnectionEntry { Name = "db", Host = "db.internal" };
        var direct = new ConnectionEntry { Name = "direct", Host = "d.example", Settings = { JumpHostId = InheritableSettings.NoJumpHost } };
        tree.Add(bastion, tree.Root);
        tree.Add(folder, tree.Root);
        tree.Add(db, folder);
        tree.Add(direct, folder);

        Assert.Null(ProbeTarget.For(db, out var why));
        Assert.Contains("jump host", why);
        Assert.NotNull(ProbeTarget.For(direct, out _));
        Assert.NotNull(ProbeTarget.For(bastion, out _));
    }

    [Fact]
    public void A_guest_behind_its_hosts_jump_host_is_not_checked()
    {
        var tree = ConnectionTree.Empty();
        var bastion = new ConnectionEntry { Name = "bastion", Host = "b.example", Settings = { Protocol = Protocol.Ssh } };
        var host = new ConnectionEntry { Name = "hv", Host = "hv.internal", Settings = { JumpHostId = bastion.Id } };
        var guest = new ConnectionEntry { Name = "vm", Host = "vm.internal" };
        tree.Add(bastion, tree.Root);
        tree.Add(host, tree.Root);
        tree.AddGuest(guest, host);

        Assert.Null(ProbeTarget.For(guest, out var why));
        Assert.Contains("jump host", why);
    }

    [Fact]
    public void A_folder_with_host_checks_off_stops_them_for_everything_under_it_unless_one_turns_them_back_on()
    {
        var tree = ConnectionTree.Empty();
        var imported = new ConnectionFolder { Name = "From Sam", Settings = { CheckHostStatus = false } };
        var host = new ConnectionEntry { Name = "hv", Host = "hv.example" };
        var guest = new ConnectionEntry { Name = "vm", Host = "vm.example" };
        var mine = new ConnectionEntry { Name = "mine", Host = "mine.example", Settings = { CheckHostStatus = true } };
        tree.Add(imported, tree.Root);
        tree.Add(host, imported);
        tree.Add(mine, imported);
        tree.AddGuest(guest, host);

        Assert.Null(ProbeTarget.For(host, out var why));
        Assert.Contains("From Sam", why);
        Assert.Null(ProbeTarget.For(guest, out _));
        Assert.NotNull(ProbeTarget.For(mine, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("host:notaport")]
    public void A_connection_without_a_usable_host_is_not_checked(string host)
    {
        Assert.Null(ProbeTarget.For(new ConnectionEntry { Host = host }, out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    // ---- The monitor ----

    [Fact]
    public async Task Results_are_kept_and_only_old_or_missing_ones_are_probed_again()
    {
        var time = new ManualTime();
        var probe = new FakeProbe((host, _) => host != "down.example");
        var monitor = new HostStatusMonitor(probe, time: time);
        var up = Target("up.example");
        var down = Target("down.example");

        await monitor.CheckAsync([up, down], TimeSpan.FromSeconds(60), default);
        Assert.Equal(HostState.Up, monitor.Get(up)!.State);
        Assert.Equal(HostState.Down, monitor.Get(down)!.State);
        Assert.Equal(2, probe.Calls);

        time.Advance(TimeSpan.FromSeconds(30));
        await monitor.CheckAsync([up, down], TimeSpan.FromSeconds(60), default);
        Assert.Equal(2, probe.Calls);

        time.Advance(TimeSpan.FromSeconds(31));
        await monitor.CheckAsync([up, down], TimeSpan.FromSeconds(60), default);
        Assert.Equal(4, probe.Calls);
    }

    [Fact]
    public async Task At_most_the_limit_of_probes_run_at_once()
    {
        var probe = new FakeProbe();
        var monitor = new HostStatusMonitor(probe, maxConcurrency: 3);

        await monitor.CheckAsync(Enumerable.Range(0, 20).Select(i => Target($"h{i}.example")), TimeSpan.Zero, default);

        Assert.Equal(20, probe.Calls);
        Assert.InRange(probe.Peak, 1, 3);
    }

    [Fact]
    public async Task A_probe_still_running_is_not_started_twice()
    {
        var probe = new FakeProbe { Gate = new TaskCompletionSource() };
        var monitor = new HostStatusMonitor(probe);
        var target = Target();

        var first = monitor.CheckAsync([target], TimeSpan.Zero, default);
        await monitor.CheckAsync([target], TimeSpan.Zero, default);
        Assert.Equal(1, probe.Calls);

        probe.Gate.SetResult();
        await first;
        Assert.Equal(HostState.Up, monitor.Get(target)!.State);
    }

    [Fact]
    public async Task An_answer_for_an_old_host_or_port_is_not_shown_for_a_new_one()
    {
        var monitor = new HostStatusMonitor(new FakeProbe());
        var target = Target("old.example");
        await monitor.CheckAsync([target], TimeSpan.Zero, default);

        Assert.NotNull(monitor.Get(target));
        Assert.Null(monitor.Get(target with { Host = "new.example" }));
        Assert.Null(monitor.Get(target with { Port = 2222 }));
    }

    [Fact]
    public async Task Canceling_stops_probes_and_records_nothing()
    {
        var probe = new FakeProbe { Gate = new TaskCompletionSource() };
        var monitor = new HostStatusMonitor(probe);
        var target = Target();
        using var cancel = new CancellationTokenSource();

        var running = monitor.CheckAsync([target], TimeSpan.Zero, cancel.Token);
        cancel.Cancel();
        await running;

        Assert.Null(monitor.Get(target));
    }

    [Fact]
    public async Task One_call_probes_no_more_than_the_cap()
    {
        var probe = new FakeProbe();
        var monitor = new HostStatusMonitor(probe);

        await monitor.CheckAsync(Enumerable.Range(0, 500).Select(i => Target($"h{i}")), TimeSpan.Zero, default);

        Assert.Equal(HostStatusMonitor.MaxTargetsPerCall, probe.Calls);
    }

    [Fact]
    public async Task Clear_forgets_every_answer()
    {
        var monitor = new HostStatusMonitor(new FakeProbe());
        var target = Target();
        await monitor.CheckAsync([target], TimeSpan.Zero, default);
        monitor.Clear();
        Assert.Null(monitor.Get(target));
    }

    // ---- The real probe ----

    [Fact]
    public async Task The_tcp_probe_says_up_for_a_listening_port_and_down_for_a_closed_one()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int open = ((IPEndPoint)listener.LocalEndpoint).Port;

        var probe = new TcpHostProbe(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(ProbeAnswer.Up, await probe.ProbeAsync("127.0.0.1", open, default));
        }
        finally
        {
            listener.Stop();
        }

        Assert.Equal(ProbeAnswer.Down, await probe.ProbeAsync("127.0.0.1", open, default));
        Assert.Equal(ProbeAnswer.Down, await probe.ProbeAsync("not a host name", 22, default));
    }

    [Fact]
    public async Task The_tcp_probe_throws_only_when_canceled()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TcpHostProbe().ProbeAsync("127.0.0.1", 9, cancel.Token));
    }

    // ---- Choosing how ----

    [Fact]
    public async Task Pinging_sends_one_echo_a_round_to_a_host_several_connections_share()
    {
        var probe = new FakeProbe((host, _) => host.Equals("a.example", StringComparison.OrdinalIgnoreCase), HostCheckMethod.Ping);
        var monitor = new HostStatusMonitor(probe);
        var ssh = Target("a.example", 22);
        var rdp = Target("a.example", 3389);
        var shouted = Target("A.EXAMPLE", 5900);
        var other = Target("b.example", 22);

        await monitor.CheckAsync([ssh, rdp, shouted, other], TimeSpan.FromSeconds(60), default);

        Assert.Equal(2, probe.Calls);
        Assert.All(new[] { ssh, rdp, shouted }, t => Assert.Equal(HostState.Up, monitor.Get(t)!.State));
        Assert.Equal(HostState.Down, monitor.Get(other)!.State);
        Assert.All(new[] { ssh, rdp, shouted, other }, t => Assert.Equal(HostCheckMethod.Ping, monitor.Get(t)!.Method));
    }

    [Fact]
    public async Task Tcp_shares_a_probe_only_between_connections_to_the_same_port()
    {
        var probe = new FakeProbe();
        var monitor = new HostStatusMonitor(probe);

        await monitor.CheckAsync([Target("a.example", 22), Target("a.example", 22), Target("a.example", 3389)], TimeSpan.Zero, default);

        Assert.Equal(2, probe.Calls);
        Assert.Equal(["a.example:22", "a.example:3389"], probe.Probed.Order());
    }

    [Fact]
    public async Task A_new_method_checks_again_answers_made_the_old_way_and_shows_them_until_then()
    {
        var time = new ManualTime();
        var tcp = new FakeProbe();
        var ping = new FakeProbe(method: HostCheckMethod.Ping) { Gate = new TaskCompletionSource() };
        var monitor = new HostStatusMonitor(tcp, time: time);
        var target = Target();
        await monitor.CheckAsync([target], TimeSpan.FromSeconds(60), default);
        Assert.Equal(HostCheckMethod.Tcp, monitor.Get(target)!.Method);

        monitor.Probe = ping;
        var running = monitor.CheckAsync([target], TimeSpan.FromSeconds(60), default);
        Assert.Equal(HostCheckMethod.Tcp, monitor.Get(target)!.Method);
        ping.Gate.SetResult();
        await running;

        Assert.Equal(1, tcp.Calls);
        Assert.Equal(1, ping.Calls);
        Assert.Equal(HostCheckMethod.Ping, monitor.Get(target)!.Method);
    }

    [Fact]
    public async Task A_probe_that_cannot_check_is_kept_as_unknown_with_the_reason()
    {
        var probe = new FixedProbe(new ProbeAnswer(HostState.Unknown, "no ping here"));
        var monitor = new HostStatusMonitor(probe);
        var target = Target();

        await monitor.CheckAsync([target], TimeSpan.Zero, default);

        var check = monitor.Get(target)!;
        Assert.Equal(HostState.Unknown, check.State);
        Assert.Equal("no ping here", check.WhyNot);
    }

    sealed class FixedProbe(ProbeAnswer answer) : IHostProbe
    {
        public HostCheckMethod Method => HostCheckMethod.Ping;
        public string KeyFor(string host, int port) => host;
        public Task<ProbeAnswer> ProbeAsync(string host, int port, CancellationToken cancellationToken) => Task.FromResult(answer);
    }

    [Theory]
    [InlineData(IPStatus.Success, HostState.Up)]
    [InlineData(IPStatus.TimedOut, HostState.Down)]
    [InlineData(IPStatus.DestinationHostUnreachable, HostState.Down)]
    [InlineData(IPStatus.DestinationNetworkUnreachable, HostState.Down)]
    [InlineData(IPStatus.TtlExpired, HostState.Down)]
    [InlineData(IPStatus.NoResources, HostState.Unknown)]
    [InlineData(IPStatus.HardwareError, HostState.Unknown)]
    [InlineData(IPStatus.Unknown, HostState.Unknown)]
    public void A_ping_reply_is_up_down_or_could_not_be_sent(IPStatus status, HostState expected)
    {
        var answer = PingHostProbe.Answer(status);

        Assert.Equal(expected, answer.State);
        Assert.Equal(expected == HostState.Unknown, answer.WhyNot is not null);
    }

    [Fact]
    public void Ping_failing_on_this_computer_is_cant_check_not_down()
    {
        var answer = PingHostProbe.CantPing(new PingException("An exception occurred during a Ping request.", new UnauthorizedAccessException("raw sockets")));

        Assert.Equal(HostState.Unknown, answer.State);
        Assert.Equal(PingHostProbe.NotAllowed, answer.WhyNot);
    }

    [Fact]
    public async Task The_ping_probe_never_calls_this_computer_down()
    {
        // Loopback answers a ping wherever ping works at all; where it doesn't (some Linux set-ups), that's can't check.
        var answer = await new PingHostProbe(TimeSpan.FromSeconds(2)).ProbeAsync("127.0.0.1", 22, default);

        Assert.NotEqual(HostState.Down, answer.State);
    }

    [Fact]
    public async Task The_ping_probe_throws_only_when_canceled()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PingHostProbe().ProbeAsync("127.0.0.1", 0, cancel.Token));
    }

    [Theory]
    [InlineData(HostCheckMethod.Tcp, typeof(TcpHostProbe))]
    [InlineData(HostCheckMethod.Ping, typeof(PingHostProbe))]
    public void Each_method_has_its_probe(HostCheckMethod method, Type type)
    {
        var probe = HostProbes.For(method);

        Assert.IsType(type, probe);
        Assert.Equal(method, probe.Method);
    }
}

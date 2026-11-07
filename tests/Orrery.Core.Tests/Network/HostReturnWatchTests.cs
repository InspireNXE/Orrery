using Orrery.Core.Network;
using Xunit;

namespace Orrery.Core.Tests.Network;

public class HostReturnWatchTests
{
    /// <summary>Answers from a script, one per probe, the last one repeated; the clock moves on with each wait.</summary>
    sealed class Script(params HostState[] answers) : IHostProbe
    {
        public int Probes { get; private set; }

        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;

        public HostCheckMethod Method => HostCheckMethod.Tcp;

        public string KeyFor(string host, int port) => $"{host}:{port}";

        public Task<ProbeAnswer> ProbeAsync(string host, int port, CancellationToken cancellationToken)
        {
            var state = answers[Math.Min(Probes, answers.Length - 1)];
            Probes++;
            return Task.FromResult(new ProbeAnswer(state));
        }

        public HostReturnWatch Watch() => new(this, (wait, _) =>
        {
            Now += wait;
            return Task.CompletedTask;
        }, () => Now);
    }

    const HostState Up = HostState.Up, Down = HostState.Down, Unknown = HostState.Unknown;

    [Fact]
    public async Task A_host_that_stops_answering_twice_in_a_row_went_down()
    {
        var script = new Script(Up, Up, Down, Down);
        Assert.True(await script.Watch().GoesDownAsync("box", 3389, () => true, CancellationToken.None));
        Assert.Equal(4, script.Probes);
    }

    [Fact]
    public async Task One_lost_answer_on_a_host_still_up_isnt_it_going_down()
    {
        var script = new Script(Up, Down, Up);
        Assert.False(await script.Watch().GoesDownAsync("box", 3389, () => true, CancellationToken.None));
    }

    [Fact]
    public async Task A_host_still_answering_a_minute_on_was_a_logoff_not_it_going_down()
    {
        var script = new Script(Up);
        Assert.False(await script.Watch().GoesDownAsync("box", 3389, () => true, CancellationToken.None));
        Assert.True(script.Now - DateTimeOffset.UnixEpoch >= HostReturnWatch.DownWithin);
    }

    [Fact]
    public async Task Unknown_answers_count_as_neither()
    {
        Assert.False(await new Script(Unknown).Watch().GoesDownAsync("box", 22, () => true, CancellationToken.None));
        Assert.False(await new Script(Unknown).Watch().ComesBackAsync("box", 22, () => true, CancellationToken.None));
    }

    [Fact]
    public async Task A_host_is_back_when_it_answers_again()
    {
        var script = new Script(Down, Down, Down, Up);
        Assert.True(await script.Watch().ComesBackAsync("box", 3389, () => true, CancellationToken.None));
        Assert.Equal(4, script.Probes);
    }

    [Fact]
    public async Task Waiting_for_a_host_gives_up_after_the_longest_wait()
    {
        var script = new Script(Down);
        Assert.False(await script.Watch().ComesBackAsync("box", 3389, () => true, CancellationToken.None));
        Assert.True(script.Now - DateTimeOffset.UnixEpoch >= HostReturnWatch.WaitAtMost);
    }

    [Fact]
    public async Task Stops_as_soon_as_its_told_to()
    {
        int asked = 0;
        var script = new Script(Down);
        Assert.False(await script.Watch().ComesBackAsync("box", 3389, () => ++asked <= 3, CancellationToken.None));
        Assert.Equal(3, script.Probes);
    }
}

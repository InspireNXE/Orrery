using System.Collections.Concurrent;
using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

public class RdpInputOrderTests
{
    [Fact]
    public async Task Input_goes_out_in_the_order_it_was_queued_even_when_an_earlier_send_is_slow()
    {
        var sent = new ConcurrentQueue<int>();
        var chain = Task.CompletedTask;
        for (int i = 0; i < 50; i++)
        {
            int n = i;
            chain = RdpSession.InOrder(chain, async () =>
            {
                // Earlier sends take longer, which tasks run side by side would finish last.
                await Task.Delay(n % 5 == 0 ? 20 : 0);
                sent.Enqueue(n);
            });
        }

        await chain;

        Assert.Equal(Enumerable.Range(0, 50), sent);
    }

    [Fact]
    public async Task A_failed_send_does_not_stop_the_ones_after_it()
    {
        bool ranAfter = false;
        var chain = RdpSession.InOrder(Task.CompletedTask, () => throw new IOException("dropped"));
        chain = RdpSession.InOrder(chain, () => Task.FromCanceled(new CancellationToken(true)));
        chain = RdpSession.InOrder(chain, () =>
        {
            ranAfter = true;
            return Task.CompletedTask;
        });

        await chain;

        Assert.True(ranAfter);
    }

    [Fact]
    public void Moves_made_before_a_send_runs_are_merged_into_the_newest()
    {
        var latest = new LatestOnly<string>();

        // The first move queues a send; the ones after it only replace what that send will take.
        Assert.True(latest.Offer("10,10"));
        Assert.False(latest.Offer("20,20"));
        Assert.False(latest.Offer("30,30"));
        Assert.Equal("30,30", latest.Take());

        // Taken already, by a click that sent it first: the queued send finds nothing.
        Assert.Null(latest.Take());

        // The next move queues a send again.
        Assert.True(latest.Offer("40,40"));
    }
}

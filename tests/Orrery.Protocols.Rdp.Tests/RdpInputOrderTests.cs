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
}

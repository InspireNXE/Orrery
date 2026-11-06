using System.Collections.Concurrent;
using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

public class OwnThreadTests
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_loop_runs_on_a_thread_of_its_own_and_its_awaits_come_back_to_it()
    {
        var seen = new ConcurrentQueue<(int Id, bool Pool)>();
        void Note() => seen.Enqueue((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));

        await OwnThread.Run("test loop", async () =>
        {
            Note();
            await Task.Delay(20);
            Note();
            await Task.Yield();
            Note();

            // A blocking wait, as the clipboard's native one, holds this thread only.
            Thread.Sleep(20);
            await Task.Run(() => { });
            Note();
        }).WaitAsync(Limit);

        Assert.Equal(4, seen.Count);
        Assert.Single(seen.Select(s => s.Id).Distinct());
        Assert.All(seen, s => Assert.False(s.Pool));
        Assert.NotEqual(Environment.CurrentManagedThreadId, seen.First().Id);
    }

    [Fact]
    public async Task A_failure_in_the_loop_ends_its_task_with_it()
    {
        var run = OwnThread.Run("test loop", async () =>
        {
            await Task.Delay(10);
            throw new InvalidOperationException("broke");
        });

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Limit));
        Assert.Equal("broke", e.Message);
    }

    [Fact]
    public async Task A_loop_ended_by_cancellation_leaves_its_task_canceled()
    {
        using var cts = new CancellationTokenSource();
        var run = OwnThread.Run("test loop", async () =>
        {
            await Task.Delay(10);
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Limit));
        Assert.True(run.IsCanceled);
    }

    [Fact]
    public async Task Work_the_loop_started_that_finishes_after_it_still_runs()
    {
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task LateAsync()
        {
            await Task.Delay(50);
            late.SetResult();
        }

        await OwnThread.Run("test loop", () =>
        {
            _ = LateAsync();
            return Task.CompletedTask;
        }).WaitAsync(Limit);

        await late.Task.WaitAsync(Limit);
    }
}

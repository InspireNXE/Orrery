using System.Collections.Concurrent;

namespace Orrery.Protocols.Rdp;

/// <summary>
/// Runs an async loop that blocks its thread at times, waiting in a native call, on a thread of its own, with its
/// awaits brought back to that thread. On the thread pool, each RDP session's clipboard loop kept one pool thread
/// blocked for as long as the session lasted, so with as many sessions as the computer has processors, input and
/// pictures queued behind threads the pool adds only every half second or so.
/// </summary>
internal static class OwnThread
{
    /// <summary>Starts <paramref name="loop"/> on a new background thread; the task ends as the loop does.</summary>
    public static Task Run(string name, Func<Task> loop)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new LoopContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var task = loop();
                task.ContinueWith(_ => context.Complete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                context.RunUntilComplete();
                task.GetAwaiter().GetResult();
                done.TrySetResult();
            }
            catch (OperationCanceledException e)
            {
                done.TrySetCanceled(e.CancellationToken);
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };

        thread.Start();
        return done.Task;
    }

    /// <summary>Runs what's posted to it, in order, on the thread that called <see cref="RunUntilComplete"/>.</summary>
    sealed class LoopContext : SynchronizationContext
    {
        readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Something the loop started, finishing after the loop ended: the pool runs it.
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override SynchronizationContext CreateCopy() => this;

        public void RunUntilComplete()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Complete() => _queue.CompleteAdding();
    }
}

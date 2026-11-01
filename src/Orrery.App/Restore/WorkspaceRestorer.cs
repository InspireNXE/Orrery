using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;

namespace Orrery.App.Restore;

/// <summary>Opens the remembered tabs one after another, a short pause between, rather than all at once.</summary>
static class WorkspaceRestorer
{
    /// <summary>Between one connection starting and the next, so a screenful of hosts isn't hit in the same instant.</summary>
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Calls <paramref name="open"/> for each tab in order, waiting <paramref name="stagger"/> (through
    /// <paramref name="delay"/>) between them and not before the first. One that fails or is cancelled at its
    /// login prompt doesn't stop the rest. The result has what each returned, null where nothing opened.
    /// </summary>
    public static async Task<T?[]> OpenAllAsync<T>(IReadOnlyList<WorkspaceTab> tabs, Func<WorkspaceTab, Task<T?>> open,
        TimeSpan stagger, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancel = default) where T : class
    {
        var opened = new T?[tabs.Count];
        for (int i = 0; i < tabs.Count && !cancel.IsCancellationRequested; i++)
        {
            if (i > 0)
            {
                try
                {
                    await delay(stagger, cancel);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                opened[i] = await open(tabs[i]);
            }
            catch (Exception e)
            {
                // Whatever went wrong with one session, the others should still come back.
                Log.For("App").LogWarning(e, "Couldn't reopen session {Number} of {Count} from last time", i + 1, tabs.Count);
            }
        }

        return opened;
    }
}

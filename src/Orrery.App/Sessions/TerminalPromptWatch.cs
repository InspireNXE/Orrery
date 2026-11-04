using Orrery.Core.Terminal;

namespace Orrery.App.Sessions;

/// <summary>
/// Waits for a terminal to show a prompt: output has stopped for a moment, and the line the cursor is on looks like a
/// shell's or a password prompt. Checked on the UI thread a few times a second, which is often enough for a person.
/// </summary>
static class TerminalPromptWatch
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    /// <summary>How long output has to have stopped before a line counts as a prompt and not one still being drawn.</summary>
    internal static TimeSpan Quiet { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The prompt showing once output stops, or <see cref="TerminalPromptKind.None"/> if none has within
    /// <paramref name="timeout"/>. With <paramref name="after"/>, only output since that count (from
    /// <see cref="TerminalView.Received"/>) counts, so the prompt a command was typed at isn't taken for the next.
    /// </summary>
    public static async Task<TerminalPromptKind> WaitAsync(TerminalView terminal, TimeSpan timeout, long? after = null, Func<bool>? stop = null)
    {
        var until = DateTime.UtcNow + timeout;
        long seen = terminal.Received;
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow < until && stop?.Invoke() != true)
        {
            await Task.Delay(Tick);
            long now = terminal.Received;
            if (now != seen)
            {
                seen = now;
                quietSince = DateTime.UtcNow;
                continue;
            }

            if ((after is null || now > after) && DateTime.UtcNow - quietSince >= Quiet
                && TerminalPrompt.Classify(terminal.CursorLine()) is not TerminalPromptKind.None and var kind)
            {
                return kind;
            }
        }

        return TerminalPromptKind.None;
    }
}

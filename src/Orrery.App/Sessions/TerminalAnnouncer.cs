using System.Text;

namespace Orrery.App.Sessions;

/// <summary>The text on a terminal's screen at one moment, for <see cref="TerminalAnnouncer"/>.</summary>
/// <param name="Rows">Each row's text, top first. Rows that don't wrap onto the next have trailing blanks trimmed.</param>
/// <param name="Wrapped">Whether each row carries on onto the next one.</param>
/// <param name="Scrolled">The emulator's <c>LinesScrolled</c>: how many lines have moved off the top so far.</param>
/// <param name="CursorRow">The row the cursor is on.</param>
sealed record TerminalScreenText(string[] Rows, bool[] Wrapped, long Scrolled, int CursorRow);

/// <summary>
/// Works out what a screen reader should say about a terminal's new output. It compares each screen with the last,
/// allowing for lines that scrolled up, and keeps what's new: a row that grew says only what was added, a new or
/// rewritten row says all of itself, a row that got shorter says nothing. The echo of what the user typed is left
/// out. Output is gathered until it pauses (or for at most <see cref="MaxWait"/>), and a flood is cut to its last
/// few lines. Nothing here is logged: it is the terminal's contents.
/// </summary>
sealed class TerminalAnnouncer
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(1500);
    public const int MaxLines = 6;
    public const int MaxCharacters = 600;

    // Keys typed but not yet echoed. Kept short and briefly: a password typed at a prompt that doesn't echo sits here
    // until it expires or the output doesn't match it.
    const int MaxTyped = 256;
    static readonly TimeSpan TypedExpiry = TimeSpan.FromSeconds(2);

    TerminalScreenText? _last;
    readonly StringBuilder _typed = new();
    DateTime _typedAt;
    readonly List<string> _pending = [];
    DateTime _firstPending, _lastPending;

    public bool HasPending => _pending.Count > 0;

    /// <summary>Takes <paramref name="screen"/> as the starting point without saying anything, after a resize or while hidden.</summary>
    public void Reset(TerminalScreenText screen)
    {
        _last = screen;
        _pending.Clear();
    }

    /// <summary>The user typed <paramref name="text"/>; Enter is "\n". Its echo won't be read out.</summary>
    public void Typed(string text, DateTime now)
    {
        Expire(now);
        _typed.Append(text);
        if (_typed.Length > MaxTyped)
        {
            _typed.Remove(0, _typed.Length - MaxTyped);
        }

        _typedAt = now;
    }

    /// <summary>The user pressed Backspace: the last typed character won't be echoed after all.</summary>
    public void Erased()
    {
        if (_typed.Length > 0)
        {
            _typed.Length--;
        }
    }

    /// <summary>Compares <paramref name="screen"/> with the last one and queues what's new.</summary>
    public void Update(TerminalScreenText screen, DateTime now)
    {
        var last = _last;
        _last = screen;
        if (last is null || last.Rows.Length != screen.Rows.Length)
        {
            return;
        }

        Expire(now);
        var text = WithoutEcho(string.Join("\n", Changes(last, screen)));
        foreach (var line in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                if (_pending.Count == 0)
                {
                    _firstPending = now;
                }

                _pending.Add(line.Trim());
                _lastPending = now;
            }
        }
    }

    /// <summary>What to say now, once the output has paused or waited long enough; null if nothing is due.</summary>
    public string? TakeIfDue(DateTime now)
    {
        if (_pending.Count == 0 || (now - _lastPending < Quiet && now - _firstPending < MaxWait))
        {
            return null;
        }

        var lines = _pending.ToList();
        _pending.Clear();

        int skipped = Math.Max(0, lines.Count - MaxLines);
        lines.RemoveRange(0, skipped);
        while (lines.Count > 1 && lines.Sum(l => l.Length + 1) > MaxCharacters)
        {
            lines.RemoveAt(0);
            skipped++;
        }

        if (lines[0].Length > MaxCharacters)
        {
            lines[0] = lines[0][..MaxCharacters] + "…";
        }

        var text = string.Join("\n", lines);
        return skipped > 0 ? $"Skipped {skipped} {(skipped == 1 ? "line" : "lines")}.\n{text}" : text;
    }

    /// <summary>The new text on each row that changed, rows that wrap joined to the next.</summary>
    static List<string> Changes(TerminalScreenText last, TerminalScreenText screen)
    {
        int count = screen.Rows.Length;
        long moved = screen.Scrolled - last.Scrolled;
        int shift = moved < 0 ? 0 : (int)Math.Min(moved, count);
        var parts = new List<string>();
        int previous = -2;
        for (int i = 0; i < count; i++)
        {
            var now = screen.Rows[i];
            var before = i + shift < count ? last.Rows[i + shift] : string.Empty;
            if (now == before)
            {
                continue;
            }

            // A row below the cursor that didn't move, such as tmux's status line under a scrolling pane.
            if (shift > 0 && i > screen.CursorRow && now == last.Rows[i])
            {
                continue;
            }

            string part;
            if (now.StartsWith(before, StringComparison.Ordinal))
            {
                part = now[before.Length..];
            }
            else if (before.StartsWith(now, StringComparison.Ordinal))
            {
                continue;
            }
            else
            {
                part = now;
            }

            if (previous == i - 1 && screen.Wrapped[i - 1] && parts.Count > 0)
            {
                parts[^1] += part;
            }
            else
            {
                parts.Add(part);
            }

            previous = i;
        }

        return parts;
    }

    /// <summary><paramref name="text"/> with the echo of typed keys taken off its start.</summary>
    string WithoutEcho(string text)
    {
        int t = 0, s = 0;
        while (t < _typed.Length && s < text.Length)
        {
            if (_typed[t] == text[s])
            {
                t++;
                s++;
            }
            else if (char.IsWhiteSpace(_typed[t]))
            {
                // Enter's echo is the cursor moving down, and a typed space shows only once something follows it.
                t++;
            }
            else if (char.IsWhiteSpace(text[s]))
            {
                // A space the row had before it was trimmed, or the echo carrying on onto the next row.
                s++;
            }
            else
            {
                _typed.Clear();
                return text[s..];
            }
        }

        _typed.Remove(0, t);
        return text[s..];
    }

    void Expire(DateTime now)
    {
        if (_typed.Length > 0 && now - _typedAt > TypedExpiry)
        {
            _typed.Clear();
        }
    }
}

namespace Orrery.App.Connections;

/// <summary>
/// Something the Ctrl+K palette can do besides connecting: an item from the current session's menu, an action on
/// every open session or a folder's, one on a saved connection, or a window command.
/// </summary>
/// <param name="Title">What the row says, such as "Disconnect everything in Production".</param>
/// <param name="Group">The heading it's listed under.</param>
/// <param name="Subject">
/// The connection or folder it's about, which is in the title too. Typing only a name finds the connection itself,
/// so a command about it is offered only once a word of the query matches the rest of the title ("copy", "wake").
/// </param>
sealed record PaletteCommand(string Title, string Group, Func<Task> Run, string? Subject = null)
{
    /// <summary>Its keyboard shortcut, shown at the right.</summary>
    public string? Shortcut { get; init; }

    /// <summary>Other words it's found by, such as "preferences" for Settings.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// How well <paramref name="query"/> finds it, 0 for not at all: every word of the query has to start a word of the
    /// title or keywords. More for words matched at the start of the title, and for whole words.
    /// </summary>
    public int Score(string query)
    {
        var asked = Words(query);
        if (asked.Count == 0)
        {
            return 0;
        }

        var title = Words(Title);
        var all = title.Concat(Keywords.SelectMany(Words)).ToList();
        var subject = Subject is null ? [] : Words(Subject).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        int score = 0;
        bool verb = Subject is null;
        foreach (var word in asked)
        {
            var match = all.FirstOrDefault(w => w.StartsWith(word, StringComparison.CurrentCultureIgnoreCase));
            if (match is null)
            {
                return 0;
            }

            score += string.Equals(match, word, StringComparison.CurrentCultureIgnoreCase) ? 3 : 2;
            verb |= !subject.Contains(match);
        }

        if (!verb)
        {
            return 0;
        }

        if (title.Count > 0 && title[0].StartsWith(asked[0], StringComparison.CurrentCultureIgnoreCase))
        {
            score += 2;
        }

        return score;
    }

    static List<string> Words(string text) =>
        [.. text.Split([' ', '\t', '"', '…', '(', ')', ',', ':', '/', '·'], StringSplitOptions.RemoveEmptyEntries)];
}

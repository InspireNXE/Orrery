namespace Orrery.Core.Connections;

/// <summary>How well a saved connection matches what's typed in quick connect. Higher is better; 0 is no match.</summary>
public static class ConnectionSearch
{
    const StringComparison IgnoreCase = StringComparison.CurrentCultureIgnoreCase;

    /// <summary>
    /// Name before host, start before middle. Several words match when each is somewhere in the name, host,
    /// folder, username or notes ("prod web"). Last, the letters in order anywhere in the name ("wb01" for web-01).
    /// </summary>
    public static int Score(ConnectionEntry entry, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return 0;
        }

        if (entry.Name.StartsWith(query, IgnoreCase)) return 100;
        if (entry.Host.StartsWith(query, IgnoreCase)) return 90;
        if (entry.Name.Contains(query, IgnoreCase)) return 80;
        if (entry.Host.Contains(query, IgnoreCase)) return 70;

        string[] fields = [entry.Name, entry.Host, entry.Path, SettingsResolver.Resolve(entry).Username.Value ?? string.Empty, entry.Notes ?? string.Empty];
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.All(word => fields.Any(field => field.Contains(word, IgnoreCase))))
        {
            return words.Length > 1 ? 60 : 50;
        }

        return InOrder(entry.Name, query.Replace(" ", string.Empty)) ? 30 : 0;
    }

    /// <summary>Whether every letter of <paramref name="letters"/> is in <paramref name="text"/>, in order, gaps allowed.</summary>
    static bool InOrder(string text, string letters)
    {
        int at = 0;
        foreach (var letter in letters)
        {
            at = text.IndexOf(letter.ToString(), at, IgnoreCase);
            if (at < 0)
            {
                return false;
            }

            at++;
        }

        return letters.Length > 1;
    }
}

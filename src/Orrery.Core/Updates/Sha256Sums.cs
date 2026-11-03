namespace Orrery.Core.Updates;

/// <summary>
/// A release's <c>SHA256SUMS</c>: one "&lt;hex&gt;  &lt;file name&gt;" line per package, as <c>sha256sum</c> writes
/// them (a "*" before the name, for binary mode, is allowed too).
/// </summary>
public static class Sha256Sums
{
    /// <summary>The largest SHA256SUMS read.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Each file name with its lowercase hex SHA-256. Blank lines, comments and lines that aren't a checksum are
    /// skipped; a file listed twice with different checksums makes the whole file untrustworthy.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int space = line.IndexOf(' ');
            if (line.StartsWith('#') || space != 64 || !IsSha256(line[..space]))
            {
                continue;
            }

            var name = line[(space + 1)..].TrimStart(' ');
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }

            if (name.Length == 0)
            {
                continue;
            }

            var hash = line[..space].ToLowerInvariant();
            if (sums.TryGetValue(name, out var earlier) && earlier != hash)
            {
                throw new UpdateException($"{Release.ChecksumsName} lists {name} twice with different checksums.");
            }

            sums[name] = hash;
        }

        return sums;
    }

    public static bool IsSha256(string hex) => hex.Length == 64 && hex.All(char.IsAsciiHexDigit);
}

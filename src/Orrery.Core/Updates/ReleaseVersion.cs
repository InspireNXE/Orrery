using System.Globalization;
using System.Reflection;

namespace Orrery.Core.Updates;

/// <summary>
/// A release's version, as in "0.2.0" or "0.2.0-preview.1", ordered the way Semantic Versioning orders them: a
/// preview comes before the release it leads up to. A leading "v" (as in the tag "v0.2.0") and any "+build" suffix
/// are dropped.
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<ReleaseVersion>
{
    /// <summary>The version of the Orrery that's running, from its informational version without the "+commit" suffix.</summary>
    public static ReleaseVersion Current { get; } = OfAssembly(Assembly.GetEntryAssembly());

    /// <summary>A preview (pre-release) version, like "0.2.0-preview.1".</summary>
    public bool IsPrerelease => Prerelease is not null;

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' isn't a version like 1.2.3 or 1.2.3-preview.1.");

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        var rest = text?.Trim() ?? "";
        if (rest.StartsWith('v') || rest.StartsWith('V'))
        {
            rest = rest[1..];
        }

        int plus = rest.IndexOf('+');
        if (plus >= 0)
        {
            rest = rest[..plus];
        }

        string? prerelease = null;
        int dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = rest[(dash + 1)..];
            rest = rest[..dash];
            if (!prerelease.Split('.').All(IsIdentifier))
            {
                return false;
            }
        }

        var parts = rest.Split('.');
        if (parts.Length != 3 || !parts.All(IsNumber))
        {
            return false;
        }

        version = new ReleaseVersion(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            prerelease);
        return true;
    }

    /// <summary>
    /// The version an assembly reports: its informational version (what <c>-p:Version=</c> sets), or failing that its
    /// assembly version.
    /// </summary>
    public static ReleaseVersion OfAssembly(Assembly? assembly)
    {
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(informational, out var version))
        {
            return version;
        }

        var plain = assembly?.GetName().Version;
        return plain is null ? new ReleaseVersion(0, 0, 0) : new ReleaseVersion(plain.Major, plain.Minor, Math.Max(0, plain.Build));
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int byNumbers = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byNumbers != 0)
        {
            return byNumbers;
        }

        // A release is newer than any of its previews.
        if (Prerelease is null || other.Prerelease is null)
        {
            return (Prerelease is null).CompareTo(other.Prerelease is null);
        }

        var mine = Prerelease.Split('.');
        var theirs = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            int byPart = CompareIdentifiers(mine[i], theirs[i]);
            if (byPart != 0)
            {
                return byPart;
            }
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    /// <summary>"1.2.3" or "1.2.3-preview.1", as in the package file names.</summary>
    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    /// <summary>Numbers compare as numbers and come before words; words compare by character.</summary>
    static int CompareIdentifiers(string a, string b)
    {
        bool aNumber = IsNumber(a), bNumber = IsNumber(b);
        if (aNumber && bNumber)
        {
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }

        return aNumber != bNumber ? (aNumber ? -1 : 1) : string.CompareOrdinal(a, b);
    }

    // No leading zeros, and short enough for an int.
    static bool IsNumber(string part) =>
        part.Length is > 0 and <= 9 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');

    static bool IsIdentifier(string part) =>
        part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

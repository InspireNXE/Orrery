using System.Reflection;

namespace Orrery.Core.Diagnostics;

/// <summary>
/// Which build this is: the release version and the commit it was built from. The SDK stamps both into the
/// informational version as <c>0.2.0-preview.2+4924ab4527d7…</c>; Orrery shows them as <c>0.2.0-preview.2 (4924ab4)</c>,
/// so a report or a screenshot of About says exactly which build it came from.
/// </summary>
public static class BuildInfo
{
    static readonly (string Version, string? Commit) Stamped =
        Parse(typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
              ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString());

    /// <summary>The release version, without the commit: <c>0.2.0-preview.2</c>.</summary>
    public static string Version => Stamped.Version;

    /// <summary>The commit it was built from, abbreviated as git does (seven characters), or null if none was stamped.</summary>
    public static string? Commit => Stamped.Commit;

    /// <summary>The version with its commit: <c>0.2.0-preview.2 (4924ab4)</c>, or the version alone without one.</summary>
    public static string Display => Format(Version, Commit);

    internal static string Format(string version, string? commit) => commit is null ? version : $"{version} ({commit})";

    /// <summary>Splits an informational version into the version and a short commit, if what follows '+' is a commit hash.</summary>
    internal static (string Version, string? Commit) Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return ("unknown", null);
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informational, null);
        }

        string version = informational[..plus];
        string metadata = informational[(plus + 1)..];
        // Build metadata can hold more than the commit (a dotted list); the commit is its first hex run of 7 or more.
        string first = metadata.Split('.')[0];
        bool hash = first.Length >= 7 && first.All(Uri.IsHexDigit);
        return (version, hash ? first[..7].ToLowerInvariant() : null);
    }
}

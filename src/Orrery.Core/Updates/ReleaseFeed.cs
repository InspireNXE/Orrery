using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orrery.Core.Updates;

/// <summary>
/// The list of releases, in the shape GitHub's REST API returns it
/// (<c>GET /repos/InspireNXE/Orrery/releases</c>), and the choice of which one to offer.
/// </summary>
public static class ReleaseFeed
{
    /// <summary>
    /// Points update checks at another release list instead of GitHub: an http(s) URL, or the path of a local JSON
    /// file whose download URLs may be file: URIs or paths relative to its folder. For testing updates before a
    /// release exists (build/New-TestRelease.ps1 writes one).
    /// </summary>
    public const string OverrideVariable = "ORRERY_UPDATE_FEED";

    /// <summary>Orrery's releases on GitHub, newest first.</summary>
    public static readonly Uri GitHub = new("https://api.github.com/repos/InspireNXE/Orrery/releases?per_page=20");

    /// <summary>The releases page people read, for everything older than the list holds.</summary>
    public static readonly Uri ReleasesPage = new("https://github.com/InspireNXE/Orrery/releases");

    /// <summary>The largest release list read; GitHub's for 20 releases is far smaller.</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    static readonly JsonSerializerOptions JsonOptions = new() { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Where to read releases from: <see cref="OverrideVariable"/> when it's set, otherwise GitHub.</summary>
    public static Uri Location() => Location(Environment.GetEnvironmentVariable(OverrideVariable));

    internal static Uri Location(string? overrideValue)
    {
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return GitHub;
        }

        var value = overrideValue.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp || url.IsFile))
        {
            return url;
        }

        return new Uri(Path.GetFullPath(value));
    }

    /// <summary>
    /// Reads a release list. Drafts, releases whose tag isn't a version and assets without a usable URL are left out;
    /// fields Orrery doesn't use are ignored. A relative download URL is taken relative to <paramref name="feed"/>,
    /// and a download URL must be https or use the feed's own scheme, so a list from the web can't name local files.
    /// </summary>
    public static IReadOnlyList<Release> Parse(string json, Uri feed)
    {
        List<GitHubRelease?>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<GitHubRelease?>>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new UpdateException("The release list couldn't be read.", e);
        }

        var releases = new List<Release>();
        foreach (var entry in entries ?? [])
        {
            if (entry is null || entry.Draft || !ReleaseVersion.TryParse(entry.TagName, out var version))
            {
                continue;
            }

            var assets = new List<ReleaseAsset>();
            foreach (var asset in entry.Assets ?? [])
            {
                if (asset is { Name.Length: > 0 } && DownloadUrl(asset.BrowserDownloadUrl, feed) is { } url)
                {
                    assets.Add(new ReleaseAsset(asset.Name, asset.Size, url, Sha256Digest(asset.Digest)));
                }
            }

            releases.Add(new Release(
                version,
                entry.TagName!,
                string.IsNullOrWhiteSpace(entry.Name) ? entry.TagName! : entry.Name,
                entry.Body ?? "",
                Uri.TryCreate(entry.HtmlUrl, UriKind.Absolute, out var page) && (page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp) ? page : null,
                entry.Prerelease,
                entry.PublishedAt,
                assets));
        }

        return releases;
    }

    /// <summary>
    /// The release to offer: the newest one newer than <paramref name="current"/>, leaving out previews unless
    /// <paramref name="previews"/>, and the skipped version and anything up to it. Null when there's nothing newer.
    /// </summary>
    public static Release? Newest(IEnumerable<Release> releases, ReleaseVersion current, bool previews, string? skippedVersion)
    {
        ReleaseVersion.TryParse(skippedVersion, out var skipped);
        return releases
            .Where(r => (previews || !r.IsPreview) && r.Version > current && (skipped is null || r.Version > skipped))
            .MaxBy(r => r.Version);
    }

    static Uri? DownloadUrl(string? text, Uri feed)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) && !Uri.TryCreate(feed, text, out url))
        {
            return null;
        }

        return url.Scheme == Uri.UriSchemeHttps || url.Scheme == feed.Scheme ? url : null;
    }

    /// <summary>"sha256:&lt;hex&gt;" as lowercase hex; null for anything else.</summary>
    static string? Sha256Digest(string? digest)
    {
        const string Prefix = "sha256:";
        if (digest is null || !digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[Prefix.Length..].Trim();
        return Sha256Sums.IsSha256(hex) ? hex.ToLowerInvariant() : null;
    }

    sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset?>? Assets { get; set; }
    }

    sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}

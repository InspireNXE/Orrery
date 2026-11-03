namespace Orrery.Core.Updates;

/// <summary>One release from the release list, with only what Orrery uses.</summary>
public sealed record Release(
    ReleaseVersion Version,
    string Tag,
    string Name,
    string Notes,
    Uri? Page,
    bool Prerelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The file listing each package's SHA-256, which every release must carry.</summary>
    public const string ChecksumsName = "SHA256SUMS";

    /// <summary>A preview: marked a pre-release on GitHub, or with a version like "0.2.0-preview.1".</summary>
    public bool IsPreview => Prerelease || Version.IsPrerelease;

    public ReleaseAsset? Checksums => Asset(ChecksumsName);

    public ReleaseAsset? Asset(string name) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// A file attached to a release. <see cref="Sha256"/> is the lowercase hex digest GitHub reports for it, when it
/// reports one.
/// </summary>
public sealed record ReleaseAsset(string Name, long Size, Uri DownloadUrl, string? Sha256);

/// <summary>Something went wrong checking for, downloading or applying an update. The message is fit to show.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

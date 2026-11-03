using System.Security.Cryptography;
using Orrery.Core.Settings;
using Orrery.Core.Storage;

namespace Orrery.Core.Updates;

/// <summary>A newer release, and the file this copy would update from (null when it can only point at the release page).</summary>
public sealed record UpdateOffer(Release Release, ReleaseAsset? Asset)
{
    /// <summary>Orrery can download and apply this one itself; otherwise offer <see cref="Release.Page"/>.</summary>
    public bool CanInstall => Asset is not null;
}

/// <summary>A release file downloaded and checked against the release's SHA256SUMS.</summary>
public sealed record DownloadedUpdate(Release Release, ReleaseAsset Asset, string FilePath, string Sha256);

/// <summary>
/// What to start, just before Orrery exits, to finish an update: the new Orrery itself after a portable or AppImage
/// swap, or Orrery Setup, which installs silently and starts Orrery again.
/// </summary>
public sealed record UpdateCommand(string FileName, IReadOnlyList<string> Arguments, bool RunsSetup)
{
    /// <summary>
    /// Orrery's own restart flag (App.RestartedFlag): the new process waits for this one to let go of the data folder.
    /// </summary>
    public const string RestartedFlag = "--restarted";

    /// <summary>Orrery Setup's arguments for an update: silent, closing Orrery if it's still open, and starting it again after.</summary>
    public static readonly IReadOnlyList<string> SetupArguments =
        ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1"];
}

/// <summary>
/// Checks for, downloads and applies updates for this copy of Orrery. It reads releases from GitHub, or from
/// <see cref="ReleaseFeed.OverrideVariable"/> when that's set. Settings are the caller's: it reads the ones passed and
/// writes none.
/// </summary>
public sealed class Updater : IDisposable
{
    /// <summary>The largest release file downloaded.</summary>
    public const long MaxDownloadBytes = 1024L * 1024 * 1024;

    const string UpdatesFolder = "updates";
    const string PartialExtension = ".partial";

    readonly UpdateSource _source;
    readonly string _updatesDir;

    /// <summary>For the running Orrery: its copy, version and cache folder, and the release list it should read.</summary>
    public Updater(DataLocation location)
        : this(ThisCopy.Detect(location), ReleaseVersion.Current, ReleaseFeed.Location(), location.CacheDir)
    {
    }

    public Updater(ThisCopy copy, ReleaseVersion currentVersion, Uri feed, string cacheDir)
    {
        Copy = copy;
        CurrentVersion = currentVersion;
        Feed = feed;
        _updatesDir = Path.Combine(cacheDir, UpdatesFolder);
        _source = new UpdateSource(currentVersion);
    }

    public ThisCopy Copy { get; }

    public ReleaseVersion CurrentVersion { get; }

    /// <summary>The release list read.</summary>
    public Uri Feed { get; }

    /// <summary>The release list isn't GitHub's: <see cref="ReleaseFeed.OverrideVariable"/> points somewhere else.</summary>
    public bool UsesOtherFeed => Feed != ReleaseFeed.GitHub;

    public void Dispose() => _source.Dispose();

    /// <summary>Every release in the list, drafts left out.</summary>
    public async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        var json = await _source.ReadTextAsync(Feed, "application/vnd.github+json", ReleaseFeed.MaxBytes, cancellationToken);
        return ReleaseFeed.Parse(json, Feed);
    }

    /// <summary>The newer release the settings allow (previews, the skipped version), or null when Orrery is up to date.</summary>
    public Task<UpdateOffer?> CheckAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        CheckAsync(settings.OffersPreviewUpdates, settings.SkippedUpdateVersion, cancellationToken);

    public async Task<UpdateOffer?> CheckAsync(bool previews, string? skippedVersion, CancellationToken cancellationToken = default)
    {
        var newest = ReleaseFeed.Newest(await GetReleasesAsync(cancellationToken), CurrentVersion, previews, skippedVersion);
        return newest is null ? null : OfferFor(newest);
    }

    /// <summary>
    /// The offer for a release: with this copy's file when the release has it and its SHA256SUMS, otherwise without,
    /// so the release page is all there is to offer.
    /// </summary>
    public UpdateOffer OfferFor(Release release)
    {
        var asset = Copy.AssetName(release.Version) is { } name && release.Checksums is not null ? release.Asset(name) : null;
        return new UpdateOffer(release, asset);
    }

    /// <summary>
    /// Downloads the offer's file into the cache (<c>updates/&lt;version&gt;</c>), reporting the bytes received so far,
    /// and checks its size and SHA-256 against the release's SHA256SUMS and GitHub's digest. A file that doesn't match
    /// is deleted. A file already there from before is used only if it still matches.
    /// </summary>
    public async Task<DownloadedUpdate> DownloadAsync(UpdateOffer offer, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var release = offer.Release;
        var asset = offer.Asset ?? throw new InvalidOperationException("This update can't be installed from here; offer its release page.");
        var checksums = release.Checksums ?? throw new UpdateException($"Release {release.Tag} has no {Release.ChecksumsName}, so its files can't be checked.");

        var sums = Sha256Sums.Parse(await _source.ReadTextAsync(checksums.DownloadUrl, "application/octet-stream", Sha256Sums.MaxBytes, cancellationToken));
        if (!sums.TryGetValue(asset.Name, out var expected))
        {
            throw new UpdateException($"{Release.ChecksumsName} doesn't list {asset.Name}, so it can't be checked.");
        }

        if (asset.Sha256 is { } digest && digest != expected)
        {
            throw new UpdateException($"GitHub's checksum for {asset.Name} doesn't match {Release.ChecksumsName}. Nothing was downloaded.");
        }

        if (asset.Size > MaxDownloadBytes)
        {
            throw new UpdateException($"{asset.Name} is larger than an update should be.");
        }

        var folder = Path.Combine(_updatesDir, release.Version.ToString());
        var path = Path.Combine(folder, asset.Name);
        var update = new DownloadedUpdate(release, asset, path, expected);
        try
        {
            PrivateStorage.CreateDirectory(folder);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length == asset.Size && await HashAsync(path, cancellationToken) == expected)
                {
                    CheckSignature(update);
                    progress?.Report(asset.Size);
                    return update;
                }

                File.Delete(path);
            }

            var partial = path + PartialExtension;
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var file = new FileStream(partial, PrivateStorage.Private(new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                })))
                {
                    long received = await _source.CopyAsync(asset.DownloadUrl, "application/octet-stream", MaxDownloadBytes, asset.Size,
                        async (block, token) =>
                        {
                            hash.AppendData(block.Span);
                            await file.WriteAsync(block, token);
                        },
                        progress, cancellationToken);

                    if (received != asset.Size)
                    {
                        throw new UpdateException($"{asset.Name} was cut short: {received:N0} of {asset.Size:N0} bytes arrived.");
                    }
                }

                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
                {
                    throw new UpdateException($"{asset.Name} doesn't match its checksum, so it was deleted. Try again later.");
                }

                File.Move(partial, path, overwrite: true);
            }
            catch
            {
                File.Delete(partial);
                throw;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Couldn't save {asset.Name}: {e.Message}", e);
        }

        CheckSignature(update);
        return update;
    }

    /// <summary>
    /// Checks the downloaded file once more, then does what can be done while Orrery runs: a portable copy or an
    /// AppImage has its file swapped (the old one goes at the next start); Orrery Setup is left to run. Start the
    /// returned command, then exit. Orrery Setup should be started through the shell without asking for elevation
    /// itself: it asks for administrator rights on its own, which lets it start Orrery again as the user.
    /// </summary>
    public UpdateCommand Apply(DownloadedUpdate update)
    {
        string actual;
        try
        {
            using var file = File.OpenRead(update.FilePath);
            actual = Convert.ToHexStringLower(SHA256.HashData(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Couldn't read {update.Asset.Name}: {e.Message}", e);
        }

        if (actual != update.Sha256)
        {
            throw new UpdateException($"{update.Asset.Name} has changed since it was downloaded, so it wasn't used.");
        }

        CheckSignature(update);
        switch (Copy.Kind)
        {
            case CopyKind.WindowsSetup:
                return new UpdateCommand(update.FilePath, UpdateCommand.SetupArguments, RunsSetup: true);

            case CopyKind.WindowsPortable or CopyKind.LinuxPortable:
                UpdateFiles.ApplyPortable(update.FilePath, Copy.TargetPath!, Path.GetDirectoryName(update.FilePath)!);
                return new UpdateCommand(Copy.TargetPath!, [UpdateCommand.RestartedFlag], RunsSetup: false);

            case CopyKind.AppImage:
                UpdateFiles.Replace(Copy.TargetPath!, update.FilePath, []);
                return new UpdateCommand(Copy.TargetPath!, [UpdateCommand.RestartedFlag], RunsSetup: false);

            default:
                throw new InvalidOperationException($"A {Copy.Kind} copy doesn't update itself.");
        }
    }

    /// <summary>
    /// Clears up after an earlier update, for calling at start-up: the executable or AppImage set aside, and downloads
    /// for this version or older. Downloads of newer versions stay, ready to install. Best effort: what's still in use is
    /// tried again next start.
    /// </summary>
    public void RemoveLeftovers()
    {
        if (Copy.TargetPath is { } target)
        {
            UpdateFiles.RemoveLeftovers(target);
        }

        try
        {
            if (!Directory.Exists(_updatesDir))
            {
                return;
            }

            foreach (var folder in Directory.GetDirectories(_updatesDir))
            {
                if (ReleaseVersion.TryParse(Path.GetFileName(folder), out var version) && version > CurrentVersion)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Orrery Setup may still be finishing.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache can't be listed; nothing to clear that matters.
        }
    }

    /// <summary>
    /// Where a check of the file's signature goes (Authenticode for Orrery Setup on Windows, say) once releases are
    /// signed. Releases aren't signed yet, so it checks nothing; the SHA-256 checks above are what protect the update.
    /// </summary>
    static void CheckSignature(DownloadedUpdate update)
    {
    }

    static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
    }
}

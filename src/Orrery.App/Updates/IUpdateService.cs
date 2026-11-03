using Orrery.Core.Updates;

namespace Orrery.App.Updates;

/// <summary>
/// The update engine's work, as the update screens use it: <see cref="EngineUpdateService"/> in Orrery, a stand-in in
/// tests, so the screens can be tried without a network.
/// </summary>
interface IUpdateService : IDisposable
{
    ReleaseVersion CurrentVersion { get; }

    /// <summary>How this copy was installed, which says why an update can only be offered as a page.</summary>
    CopyKind Kind { get; }

    /// <summary>Releases come from somewhere other than GitHub (ORRERY_UPDATE_FEED), as when testing an update.</summary>
    bool UsesOtherFeed { get; }

    /// <summary>The newer release to offer, or null when Orrery is up to date. Fails with <see cref="UpdateException"/>.</summary>
    Task<UpdateOffer?> CheckAsync(bool previews, string? skippedVersion, CancellationToken cancellationToken);

    /// <summary>Downloads and checks the offer's file, reporting the bytes received so far.</summary>
    Task<DownloadedUpdate> DownloadAsync(UpdateOffer offer, IProgress<long>? progress, CancellationToken cancellationToken);

    /// <summary>Does what can be done while Orrery runs, and says what to start just before it exits.</summary>
    UpdateCommand Apply(DownloadedUpdate update);

    /// <summary>Clears up after an earlier update. Best effort.</summary>
    void RemoveLeftovers();
}

/// <summary>The real engine: GitHub's releases (or ORRERY_UPDATE_FEED's) for this copy of Orrery.</summary>
sealed class EngineUpdateService(Updater updater) : IUpdateService
{
    public ReleaseVersion CurrentVersion => updater.CurrentVersion;

    public CopyKind Kind => updater.Copy.Kind;

    public bool UsesOtherFeed => updater.UsesOtherFeed;

    public Task<UpdateOffer?> CheckAsync(bool previews, string? skippedVersion, CancellationToken cancellationToken) =>
        updater.CheckAsync(previews, skippedVersion, cancellationToken);

    public Task<DownloadedUpdate> DownloadAsync(UpdateOffer offer, IProgress<long>? progress, CancellationToken cancellationToken) =>
        updater.DownloadAsync(offer, progress, cancellationToken);

    public UpdateCommand Apply(DownloadedUpdate update) => updater.Apply(update);

    public void RemoveLeftovers() => updater.RemoveLeftovers();

    public void Dispose() => updater.Dispose();
}

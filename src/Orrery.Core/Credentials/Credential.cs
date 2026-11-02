namespace Orrery.Core.Credentials;

public enum CredentialKind
{
    Password,
    SshKey,
    SshKeyWithPassphrase,
}

/// <summary>
/// A saved login. Connections refer to it by <see cref="Id"/> and never hold the secret themselves,
/// so a password rotation is one edit. See docs/design-storage-vault-import.md §3.
/// </summary>
public sealed record Credential(
    Guid Id,
    string Name,
    CredentialKind Kind,
    string? Username,
    string? Domain,
    string Secret,
    string? Passphrase = null,
    string? Notes = null)
{
    public CredentialSummary Summary => new(Id, Name, Kind, Username, Domain, Notes);

    /// <summary>
    /// Ids Orrery keeps its own secrets under (the recordings key). Stores leave them out of <see cref="ICredentialStore.ListAsync"/>,
    /// so they're never shown, picked or exported, but <see cref="ICredentialStore.GetAsync"/> finds them and
    /// <see cref="CredentialCopier"/> carries them to a new store.
    /// </summary>
    public static readonly IReadOnlySet<Guid> ReservedIds = new HashSet<Guid> { Recording.RecordingKeys.CredentialId };

    /// <summary>Keeps secrets out of logs and crash reports.</summary>
    public override string ToString() => $"Credential {{ Id = {Id}, Name = {Name}, Kind = {Kind}, Username = {Username} }}";
}

/// <summary>Everything about a credential except its secret; safe to show and to list while locked.</summary>
public sealed record CredentialSummary(
    Guid Id,
    string Name,
    CredentialKind Kind,
    string? Username,
    string? Domain,
    string? Notes)
{
    public string DisplayUser => Domain is { Length: > 0 } ? $"{Domain}\\{Username}" : Username ?? string.Empty;
}

public sealed class VaultLockedException() : InvalidOperationException("The vault is locked.");

public sealed class WrongVaultPasswordException() : Exception("The master password is incorrect.");

public interface ICredentialStore
{
    /// <summary>True when secrets can't be read until <see cref="UnlockAsync"/> succeeds.</summary>
    bool IsLocked { get; }

    /// <summary>False for a store whose first unlock creates it (a vault file that doesn't exist yet).</summary>
    bool Exists { get; }

    /// <summary>Opens the store, or creates it when <see cref="Exists"/> is false.</summary>
    /// <exception cref="WrongVaultPasswordException">The password doesn't open the vault.</exception>
    Task UnlockAsync(string masterPassword);

    void Lock();

    Task<IReadOnlyList<CredentialSummary>> ListAsync();
    Task<Credential?> GetAsync(Guid id);
    Task SaveAsync(Credential credential);

    /// <summary>
    /// Saves many at once (an import) with one rewrite of the store's file. If it fails, credentials that
    /// weren't in the store before aren't left in it.
    /// </summary>
    Task SaveAllAsync(IReadOnlyList<Credential> credentials);

    Task DeleteAsync(Guid id);
}

namespace Orrery.Core.Security;

/// <summary>What the user sees when an SSH server's key needs a decision.</summary>
public sealed record HostKeyPrompt(
    CertificatePromptKind Kind,
    string Host,
    int Port,
    string KeyType,
    string Sha256Fingerprint,
    string? PinnedFingerprint);

/// <summary>
/// Trust on first use for SSH host keys, like OpenSSH's known_hosts: the first key a host presents is
/// shown for the user to confirm and then pinned; a different key later gets a warning. There's no
/// authority to vouch for SSH keys, so unknown hosts always ask.
/// </summary>
public sealed class HostKeyValidator(ICertificateTrustStore store, Func<HostKeyPrompt, Task<CertificateTrustDecision>> prompt)
{
    public async Task<bool> ValidateAsync(string host, int port, string keyType, string sha256Fingerprint) =>
        (await CheckAsync(host, port, keyType, sha256Fingerprint)).Trusted;

    /// <summary>As <see cref="ValidateAsync"/>, and whether the host had a different key pinned.</summary>
    public async Task<(bool Trusted, bool Changed)> CheckAsync(string host, int port, string keyType, string sha256Fingerprint)
    {
        var pinned = store.Get(host, port);
        if (pinned is not null && string.Equals(pinned.Sha256, sha256Fingerprint, StringComparison.Ordinal))
        {
            return (true, false);
        }

        var decision = await prompt(new HostKeyPrompt(
            pinned is null ? CertificatePromptKind.Unknown : CertificatePromptKind.Changed,
            host, port, keyType, sha256Fingerprint, pinned?.Sha256));

        if (decision == CertificateTrustDecision.TrustAlways)
        {
            store.Pin(host, port, new PinnedCertificate(sha256Fingerprint, keyType, DateTimeOffset.UtcNow));
        }

        return (decision != CertificateTrustDecision.Reject, pinned is not null);
    }
}

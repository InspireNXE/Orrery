using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Orrery.Core.Security;

public enum CertificateTrustDecision
{
    Reject,
    TrustOnce,
    TrustAlways,
}

public enum CertificatePromptKind
{
    /// <summary>First connection to this host, and the certificate isn't from a trusted authority.</summary>
    Unknown,

    /// <summary>The host presented a different certificate from the one pinned earlier.</summary>
    Changed,
}

/// <summary>What the user is shown when a server certificate needs a decision.</summary>
public sealed record CertificatePrompt(
    CertificatePromptKind Kind,
    string Host,
    int Port,
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Sha256,
    string? PinnedSha256,
    IReadOnlyList<string> Problems);

public sealed record PinnedCertificate(string Sha256, string Subject, DateTimeOffset PinnedAt);

public interface ICertificateTrustStore
{
    PinnedCertificate? Get(string host, int port);
    void Pin(string host, int port, PinnedCertificate certificate);
}

/// <summary>
/// Decides whether to continue a connection after TLS, before any credentials are sent.
/// Certificates from a trusted authority that match the host pass. Anything else must match the
/// certificate pinned for that host, or the user is asked.
/// </summary>
public sealed class ServerCertificateValidator(
    ICertificateTrustStore store,
    Func<CertificatePrompt, Task<CertificateTrustDecision>> prompt)
{
    public async Task<bool> ValidateAsync(string host, int port, X509Certificate2 certificate, SslPolicyErrors errors)
    {
        var sha256 = Sha256Of(certificate);
        var pinned = store.Get(host, port);

        if (pinned is not null && string.Equals(pinned.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (pinned is null && errors == SslPolicyErrors.None)
        {
            return true;
        }

        var request = new CertificatePrompt(
            pinned is null ? CertificatePromptKind.Unknown : CertificatePromptKind.Changed,
            host,
            port,
            certificate.Subject,
            certificate.Issuer,
            certificate.NotBefore,
            certificate.NotAfter,
            sha256,
            pinned?.Sha256,
            DescribeProblems(host, certificate, errors));

        var decision = await prompt(request);
        if (decision == CertificateTrustDecision.TrustAlways)
        {
            store.Pin(host, port, new PinnedCertificate(sha256, certificate.Subject, DateTimeOffset.UtcNow));
        }

        return decision != CertificateTrustDecision.Reject;
    }

    public static string Sha256Of(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    static List<string> DescribeProblems(string host, X509Certificate2 certificate, SslPolicyErrors errors)
    {
        var problems = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            problems.Add("It isn't issued by an authority this computer trusts. Remote desktop servers often use self-signed certificates.");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            problems.Add($"It isn't issued for \"{host}\".");
        }

        var now = DateTime.Now;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
        {
            problems.Add("It has expired or isn't valid yet.");
        }

        return problems;
    }
}

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Orrery.Core.Security;
using Xunit;

namespace Orrery.Core.Tests.Security;

public class ServerCertificateValidatorTests : IDisposable
{
    const string Host = "web-01.corp.local";
    const int Port = 3389;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly List<CertificatePrompt> _prompts = [];
    CertificateTrustDecision _answer = CertificateTrustDecision.Reject;

    string StorePath => Path.Combine(_dir, "trusted-certificates.json");

    ServerCertificateValidator CreateValidator(ICertificateTrustStore? store = null) =>
        new(store ?? new JsonCertificateTrustStore(StorePath), prompt =>
        {
            _prompts.Add(prompt);
            return Task.FromResult(_answer);
        });

    static X509Certificate2 SelfSigned(string name = Host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public async Task Certificate_from_trusted_authority_passes_without_prompt_or_pin()
    {
        var store = new JsonCertificateTrustStore(StorePath);
        var ok = await CreateValidator(store).ValidateAsync(Host, Port, SelfSigned(), SslPolicyErrors.None);

        Assert.True(ok);
        Assert.Empty(_prompts);
        Assert.Null(store.Get(Host, Port));
    }

    [Fact]
    public async Task Unknown_untrusted_certificate_prompts_and_reject_fails()
    {
        var ok = await CreateValidator().ValidateAsync(Host, Port, SelfSigned(), SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(ok);
        var prompt = Assert.Single(_prompts);
        Assert.Equal(CertificatePromptKind.Unknown, prompt.Kind);
        Assert.NotEmpty(prompt.Problems);
    }

    [Fact]
    public async Task Trust_always_pins_and_later_connections_pass_silently()
    {
        var cert = SelfSigned();
        _answer = CertificateTrustDecision.TrustAlways;
        Assert.True(await CreateValidator().ValidateAsync(Host, Port, cert, SslPolicyErrors.RemoteCertificateChainErrors));

        // A fresh store reads the pin back from disk.
        _prompts.Clear();
        _answer = CertificateTrustDecision.Reject;
        Assert.True(await CreateValidator().ValidateAsync(Host, Port, cert, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task Trust_once_does_not_pin()
    {
        var cert = SelfSigned();
        _answer = CertificateTrustDecision.TrustOnce;
        Assert.True(await CreateValidator().ValidateAsync(Host, Port, cert, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(await CreateValidator().ValidateAsync(Host, Port, cert, SslPolicyErrors.RemoteCertificateChainErrors));

        Assert.Equal(2, _prompts.Count);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task Changed_certificate_prompts_even_when_issued_by_trusted_authority()
    {
        var store = new JsonCertificateTrustStore(StorePath);
        var original = SelfSigned();
        store.Pin(Host, Port, new PinnedCertificate(ServerCertificateValidator.Sha256Of(original), original.Subject, DateTimeOffset.UtcNow));

        var ok = await CreateValidator(store).ValidateAsync(Host, Port, SelfSigned(), SslPolicyErrors.None);

        Assert.False(ok);
        var prompt = Assert.Single(_prompts);
        Assert.Equal(CertificatePromptKind.Changed, prompt.Kind);
        Assert.Equal(ServerCertificateValidator.Sha256Of(original), prompt.PinnedSha256);
    }

    [Fact]
    public async Task Pins_are_per_host_and_port()
    {
        var cert = SelfSigned();
        _answer = CertificateTrustDecision.TrustAlways;
        await CreateValidator().ValidateAsync(Host, Port, cert, SslPolicyErrors.RemoteCertificateChainErrors);

        _prompts.Clear();
        _answer = CertificateTrustDecision.Reject;
        Assert.False(await CreateValidator().ValidateAsync(Host, 3390, cert, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(await CreateValidator().ValidateAsync(Host.ToUpperInvariant(), Port, cert, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Single(_prompts);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

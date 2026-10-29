using Orrery.Core.Security;
using Xunit;

namespace Orrery.Core.Tests.Security;

public class HostKeyValidatorTests : IDisposable
{
    const string Host = "db-01";
    const string Key = "SHA256:abc123";

    readonly string _path = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"), "known-hosts.json");
    readonly List<HostKeyPrompt> _prompts = [];
    CertificateTrustDecision _answer;

    HostKeyValidator Validator() => new(new JsonCertificateTrustStore(_path), p =>
    {
        _prompts.Add(p);
        return Task.FromResult(_answer);
    });

    [Fact]
    public async Task First_connection_always_asks_and_trust_always_pins()
    {
        _answer = CertificateTrustDecision.TrustAlways;
        Assert.True(await Validator().ValidateAsync(Host, 22, "ssh-ed25519", Key));
        Assert.Equal(CertificatePromptKind.Unknown, Assert.Single(_prompts).Kind);

        _prompts.Clear();
        Assert.True(await Validator().ValidateAsync(Host, 22, "ssh-ed25519", Key));
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task Changed_key_warns_and_rejecting_fails()
    {
        _answer = CertificateTrustDecision.TrustAlways;
        await Validator().ValidateAsync(Host, 22, "ssh-ed25519", Key);

        _prompts.Clear();
        _answer = CertificateTrustDecision.Reject;
        Assert.False(await Validator().ValidateAsync(Host, 22, "ssh-ed25519", "SHA256:different"));
        var prompt = Assert.Single(_prompts);
        Assert.Equal(CertificatePromptKind.Changed, prompt.Kind);
        Assert.Equal(Key, prompt.PinnedFingerprint);
    }

    [Fact]
    public async Task Trust_once_does_not_pin()
    {
        _answer = CertificateTrustDecision.TrustOnce;
        Assert.True(await Validator().ValidateAsync(Host, 22, "ssh-rsa", Key));
        Assert.True(await Validator().ValidateAsync(Host, 22, "ssh-rsa", Key));
        Assert.Equal(2, _prompts.Count);
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

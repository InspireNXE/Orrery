using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

public class SshAlgorithmTests
{
    static readonly string[] LegacyKeyExchange = ["diffie-hellman-group1-sha1", "diffie-hellman-group14-sha1", "diffie-hellman-group-exchange-sha1"];
    static readonly string[] LegacyEncryption = ["3des-cbc", "aes128-cbc", "aes192-cbc", "aes256-cbc"];
    static readonly string[] LegacyMac = ["hmac-sha1", "hmac-sha1-etm@openssh.com"];
    static readonly string[] LegacyHostKey = ["ssh-rsa"];

    static ConnectionInfo NewInfo() => new("box.example", 22, "alice", new PasswordAuthenticationMethod("alice", "x"));

    static ConnectionInfo Applied(bool allowLegacy)
    {
        var info = NewInfo();
        SshAlgorithms.Apply(info, allowLegacy);
        return info;
    }

    [Fact]
    public void Every_name_we_remove_exists_in_the_default_lists()
    {
        // A renamed or dropped name must fail here rather than be skipped without anyone noticing.
        var defaults = NewInfo();
        foreach (var name in LegacyKeyExchange) Assert.True(defaults.KeyExchangeAlgorithms.ContainsKey(name), name);
        foreach (var name in LegacyEncryption) Assert.True(defaults.Encryptions.ContainsKey(name), name);
        foreach (var name in LegacyMac) Assert.True(defaults.HmacAlgorithms.ContainsKey(name), name);
        foreach (var name in LegacyHostKey) Assert.True(defaults.HostKeyAlgorithms.ContainsKey(name), name);
    }

    [Fact]
    public void Legacy_off_drops_sha1_cbc_and_3des_and_keeps_the_modern_algorithms()
    {
        var info = Applied(allowLegacy: false);

        foreach (var name in LegacyKeyExchange) Assert.DoesNotContain(name, info.KeyExchangeAlgorithms.Keys);
        foreach (var name in LegacyEncryption) Assert.DoesNotContain(name, info.Encryptions.Keys);
        foreach (var name in LegacyMac) Assert.DoesNotContain(name, info.HmacAlgorithms.Keys);
        foreach (var name in LegacyHostKey) Assert.DoesNotContain(name, info.HostKeyAlgorithms.Keys);

        Assert.Contains("curve25519-sha256", info.KeyExchangeAlgorithms.Keys);
        Assert.Contains("diffie-hellman-group14-sha256", info.KeyExchangeAlgorithms.Keys);
        Assert.Contains("aes256-gcm@openssh.com", info.Encryptions.Keys);
        Assert.Contains("aes256-ctr", info.Encryptions.Keys);
        Assert.Contains("hmac-sha2-256-etm@openssh.com", info.HmacAlgorithms.Keys);
        Assert.Contains("hmac-sha2-512", info.HmacAlgorithms.Keys);
        Assert.Contains("ssh-ed25519", info.HostKeyAlgorithms.Keys);
        Assert.Contains("rsa-sha2-512", info.HostKeyAlgorithms.Keys);
        Assert.Contains("rsa-sha2-256", info.HostKeyAlgorithms.Keys);
    }

    [Fact]
    public void Legacy_on_leaves_the_library_defaults_untouched()
    {
        var plain = NewInfo();
        var info = Applied(allowLegacy: true);

        Assert.Equal(plain.KeyExchangeAlgorithms.Keys, info.KeyExchangeAlgorithms.Keys);
        Assert.Equal(plain.Encryptions.Keys, info.Encryptions.Keys);
        Assert.Equal(plain.HmacAlgorithms.Keys, info.HmacAlgorithms.Keys);
        Assert.Equal(plain.HostKeyAlgorithms.Keys, info.HostKeyAlgorithms.Keys);
    }

    [Theory]
    [InlineData("No matching key exchange algorithm (server offers diffie-hellman-group1-sha1)")]
    [InlineData("No matching host key algorithm (server offers ssh-rsa)")]
    [InlineData("No matching client encryption algorithm (server offers 3des-cbc,aes128-cbc)")]
    [InlineData("No matching server encryption algorithm (server offers aes256-cbc)")]
    [InlineData("No matching client MAC algorithm (server offers hmac-sha1)")]
    [InlineData("No matching server MAC algorithm (server offers hmac-sha1)")]
    public void A_failed_negotiation_with_legacy_off_points_at_the_setting(string library)
    {
        var message = SshAlgorithms.ExplainFailure(new SshConnectionException(library, DisconnectReason.KeyExchangeFailed), allowLegacy: false);

        Assert.Contains("Allow older, weaker algorithms", message);
        Assert.Contains(library, message);
    }

    [Fact]
    public void Other_failures_and_legacy_on_keep_the_library_message()
    {
        const string negotiation = "No matching key exchange algorithm (server offers diffie-hellman-group1-sha1)";
        Assert.Equal(negotiation, SshAlgorithms.ExplainFailure(new SshConnectionException(negotiation, DisconnectReason.KeyExchangeFailed), allowLegacy: true));
        Assert.Equal("Client not connected.", SshAlgorithms.ExplainFailure(new SshConnectionException("Client not connected."), allowLegacy: false));
        Assert.Equal("MAC error", SshAlgorithms.ExplainFailure(new SshConnectionException("MAC error", DisconnectReason.MacError), allowLegacy: false));

        // Compression isn't touched by the setting, so it must not suggest it.
        const string compression = "No matching client compression algorithm (server offers zlib)";
        Assert.Equal(compression, SshAlgorithms.ExplainFailure(new SshConnectionException(compression, DisconnectReason.KeyExchangeFailed), allowLegacy: false));

        // Same words, wrong reason: not a negotiation failure.
        Assert.Equal(negotiation, SshAlgorithms.ExplainFailure(new SshConnectionException(negotiation, DisconnectReason.ConnectionLost), allowLegacy: false));
    }
}

using System.Security.Cryptography;
using System.Security.Principal;
using Orrery.Protocols.Ssh.Agent;
using Renci.SshNet.Security;
using Renci.SshNet.Security.Cryptography;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

// Uses SSH_AUTH_SOCK and a window named "Pageant", both machine-wide, so these run one at a time.
[Collection("SSH agent")]
public class SshAgentTests : IDisposable
{
    readonly string? _savedSocket = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
    readonly FakeAgent _agent = new();

    SshAgentClient ServeOnPipe()
    {
        var pipe = "orrery-test-agent-" + Guid.NewGuid().ToString("N");
        _agent.ServePipe(pipe);
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", $@"\\.\pipe\{pipe}");
        return SshAgentClient.Find()!;
    }

    /// <summary>Checks a signature the way an SSH server would, using SSH.NET's own verifier.</summary>
    static bool ServerAccepts(string algorithm, byte[] publicKey, byte[] data, byte[] signature)
    {
        var keyData = new SshKeyData(publicKey);
        KeyHostAlgorithm host = algorithm switch
        {
            "rsa-sha2-512" => new KeyHostAlgorithm(algorithm, new RsaKey(keyData), new RsaDigitalSignature(new RsaKey(keyData), HashAlgorithmName.SHA512)),
            "rsa-sha2-256" => new KeyHostAlgorithm(algorithm, new RsaKey(keyData), new RsaDigitalSignature(new RsaKey(keyData), HashAlgorithmName.SHA256)),
            "ssh-rsa" => new KeyHostAlgorithm(algorithm, new RsaKey(keyData)),
            _ => new KeyHostAlgorithm(algorithm, new EcdsaKey(keyData)),
        };
        return host.VerifySignature(data, signature);
    }

    [Fact]
    public void Finds_the_agent_from_ssh_auth_sock_and_lists_its_keys()
    {
        var client = ServeOnPipe();

        var keys = client.ListIdentities();

        Assert.Contains("SSH_AUTH_SOCK", client.Description);
        Assert.Equal(["ssh-rsa", "ecdsa-sha2-nistp256"], keys.Select(k => k.KeyType));
        Assert.Equal(["dummy rsa key", "dummy ecdsa key"], keys.Select(k => k.Comment));
        Assert.Equal(_agent.RsaBlob, keys[0].PublicKey);
    }

    [Fact]
    public void Rsa_keys_offer_sha2_first_and_every_signature_verifies()
    {
        var client = ServeOnPipe();
        var rsa = client.ListIdentities()[0];
        var source = new AgentKeySource(client, rsa);
        var data = RandomNumberGenerator.GetBytes(64);

        var algorithms = source.HostKeyAlgorithms.ToList();

        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"], algorithms.Select(a => a.Name));
        foreach (var algorithm in algorithms)
        {
            Assert.Equal(rsa.PublicKey, algorithm.Data);
            Assert.True(ServerAccepts(algorithm.Name, rsa.PublicKey, data, algorithm.Sign(data)), algorithm.Name);
        }

        Assert.Equal([4u, 2u, 0u], _agent.SignFlags);
    }

    [Fact]
    public void Ecdsa_keys_sign_and_verify()
    {
        var client = ServeOnPipe();
        var ecdsa = client.ListIdentities()[1];
        var algorithm = Assert.Single(new AgentKeySource(client, ecdsa).HostKeyAlgorithms);
        var data = RandomNumberGenerator.GetBytes(64);

        Assert.Equal("ecdsa-sha2-nistp256", algorithm.Name);
        Assert.True(ServerAccepts(algorithm.Name, ecdsa.PublicKey, data, algorithm.Sign(data)));
    }

    [Fact]
    public void A_refused_signature_is_an_error_not_a_bad_signature()
    {
        var client = ServeOnPipe();
        var algorithm = new AgentKeySource(client, client.ListIdentities()[0]).HostKeyAlgorithms.First();
        _agent.RefuseSigning = true;

        Assert.Throws<Renci.SshNet.Common.SshException>(() => algorithm.Sign([1, 2, 3]));
    }

    [Fact]
    public void Talks_to_pageant_with_a_memory_block_owned_by_this_user()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Pageant is Windows-only.
        }

        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null);
        using var pageant = new FakePageant(_agent);

        var client = SshAgentClient.Find();
        Assert.NotNull(client);
        Assert.Equal("Pageant", client.Description);

        var keys = client.ListIdentities();
        var data = RandomNumberGenerator.GetBytes(32);
        var signature = new AgentKeySource(client, keys[1]).HostKeyAlgorithms.Single().Sign(data);

        Assert.Equal(2, keys.Count);
        Assert.True(ServerAccepts("ecdsa-sha2-nistp256", keys[1].PublicKey, data, signature));
        using var me = WindowsIdentity.GetCurrent();
        Assert.All(pageant.BlockOwners, owner => Assert.Equal(me.User!.Value, owner));
    }

    [Fact]
    public void No_agent_means_none_found()
    {
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null);
        if (OperatingSystem.IsWindows() && (File.Exists(@"\\.\pipe\openssh-ssh-agent") || System.Diagnostics.Process.GetProcessesByName("pageant").Length > 0))
        {
            return; // A real agent is running on this machine.
        }

        Assert.Null(SshAgentClient.Find());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", _savedSocket);
        _agent.Dispose();
    }
}

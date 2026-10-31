using System.ComponentModel;
using System.IO.Pipes;
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
        Assert.Equal("Pageant (before 0.75)", client.Description);

        var keys = client.ListIdentities();
        var data = RandomNumberGenerator.GetBytes(32);
        var signature = new AgentKeySource(client, keys[1]).HostKeyAlgorithms.Single().Sign(data);

        Assert.Equal(2, keys.Count);
        Assert.True(ServerAccepts("ecdsa-sha2-nistp256", keys[1].PublicKey, data, signature));
        using var me = WindowsIdentity.GetCurrent();
        var mySid = me.User!.Value;
        Assert.All(pageant.BlockOwners, owner => Assert.Equal(mySid, owner));
    }

    [Fact]
    public void Pageants_pipe_name_is_the_user_and_a_per_user_hash_that_stays_the_same()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = PageantPipe.Name();

        // User names can hold dots ("first.last"), so only the hash is pinned down.
        Assert.Matches(@"^pageant\.[^\\/]+\.[0-9a-f]{64}$", name);
        Assert.Equal(name, PageantPipe.Name());
    }

    [Fact]
    public void Prefers_pageants_own_pipe_to_its_window()
    {
        if (!OperatingSystem.IsWindows() || AgentOwner.PipeExists(PageantPipe.Name()))
        {
            return; // Windows-only, and a real Pageant 0.75+ is already on the pipe.
        }

        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null);
        using var window = new FakePageant(_agent);
        _agent.ServePipe(PageantPipe.Name());
        for (int i = 0; i < 100 && !AgentOwner.PipeExists(PageantPipe.Name()); i++)
        {
            Thread.Sleep(20); // The fake opens its pipe in the background.
        }

        var client = SshAgentClient.Find();
        Assert.NotNull(client);
        Assert.Equal("Pageant", client.Description);

        var keys = client.ListIdentities();
        var data = RandomNumberGenerator.GetBytes(32);
        var signature = new AgentKeySource(client, keys[1]).HostKeyAlgorithms.Single().Sign(data);
        Assert.True(ServerAccepts("ecdsa-sha2-nistp256", keys[1].PublicKey, data, signature));

        // Nothing went through the window's shared memory.
        Assert.Empty(window.BlockOwners);
    }

    [Fact]
    public void A_string_length_near_int_max_is_a_cut_short_reply()
    {
        // At position 8, the old check's 8 + length wrapped negative and slicing threw ArgumentOutOfRangeException.
        byte[] data = [0, 0, 0, 1, 0x7F, 0xFF, 0xFF, 0xF8, 1, 2, 3, 4];
        var reader = new WireReader(data);
        reader.UInt32();

        Assert.Throws<IOException>(() => reader.String());
    }

    [Fact]
    public async Task A_silent_agent_fails_the_listing_within_the_timeout()
    {
        var client = ServeOnPipe();
        client.ListTimeout = TimeSpan.FromMilliseconds(300);
        _agent.Silent = true;

        var listing = Task.Run(client.ListIdentities);

        Assert.True(await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(5))) == listing, "ListIdentities is still waiting on the agent.");
        await Assert.ThrowsAsync<TimeoutException>(() => listing);
    }

    [Fact]
    public async Task A_hung_pageant_times_out_and_does_not_block_later_requests()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Pageant is Windows-only.
        }

        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null);
        using var pageant = new FakePageant(_agent);
        var client = SshAgentClient.Find();
        Assert.NotNull(client);
        Assert.Equal("Pageant (before 0.75)", client.Description);
        client.ListTimeout = TimeSpan.FromMilliseconds(300);

        pageant.Hang();
        try
        {
            var first = Task.Run(client.ListIdentities);
            Assert.True(await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(5))) == first, "The first request is still waiting on Pageant.");
            await Assert.ThrowsAsync<TimeoutException>(() => first);

            var second = Task.Run(client.ListIdentities);
            Assert.True(await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))) == second, "The second request is blocked behind the first.");
            await Assert.ThrowsAsync<TimeoutException>(() => second);
        }
        finally
        {
            pageant.Release();
        }

        Assert.Equal(2, client.ListIdentities().Count);
    }

    [Fact]
    public void Errors_while_signing_surface_as_io_errors()
    {
        // SSH.NET lets an exception from Sign out of Connect unchanged, and the session only handles I/O-type errors.
        var client = new SshAgentClient(_agent, "fake agent");
        var algorithm = new AgentKeySource(client, client.ListIdentities()[0]).HostKeyAlgorithms.First();

        _agent.Fault = new Win32Exception(5);
        Assert.IsType<Win32Exception>(Assert.Throws<IOException>(() => algorithm.Sign([1, 2, 3])).InnerException);

        _agent.Fault = new UnauthorizedAccessException();
        Assert.IsType<UnauthorizedAccessException>(Assert.Throws<IOException>(() => algorithm.Sign([1, 2, 3])).InnerException);
    }

    [Fact]
    public async Task An_agent_run_by_this_user_passes_the_owner_check_and_another_account_does_not()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The owner check is for Windows pipes and windows.
        }

        var name = "orrery-test-owner-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        pipe.Connect(TimeSpan.FromSeconds(5));

        AgentOwner.CheckPipe(pipe.SafePipeHandle);
        Assert.True(AgentOwner.IsTrusted((uint)Environment.ProcessId, allowSystem: false));
        // A pipe owned by another account can't be made here; the System process (pid 4) stands in for one, as
        // Pageant's window owner would be checked.
        Assert.False(AgentOwner.IsTrusted(4, allowSystem: false));
        await accepted.WaitAsync(TimeSpan.FromSeconds(5));
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

using System.Net;
using System.Net.Sockets;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

public class SftpSessionTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"orrery-known-hosts-{Guid.NewGuid():N}.json");

    [Theory]
    [InlineData("/home/me", "notes.txt", "/home/me/notes.txt")]
    [InlineData("/", "etc", "/etc")]
    [InlineData("/srv/", "www", "/srv/www")]
    public void Combine_joins_with_one_forward_slash(string folder, string name, string expected) =>
        Assert.Equal(expected, SftpSession.Combine(folder, name));

    [Theory]
    [InlineData("/home/me", "/home")]
    [InlineData("/home/me/", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/", "/")]
    public void Parent_stops_at_the_root(string path, string expected) =>
        Assert.Equal(expected, SftpSession.Parent(path));

    [Theory]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    [InlineData("", "_")]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a/b", "a_b")]
    [InlineData("a\\b", "a_b")]
    [InlineData("trailing. ", "trailing")]
    public void A_server_name_cant_climb_out_of_the_download_folder(string name, string expected) =>
        Assert.Equal(expected, SftpSession.LocalName(name));

    [Fact]
    public void Lists_folders_first_then_by_name_ignoring_case()
    {
        var at = DateTime.Now;
        SftpItem File(string name) => new(name, "/" + name, false, false, 1, at, "-rw-r--r--");
        SftpItem Folder(string name) => new(name, "/" + name, true, false, 0, at, "drwxr-xr-x");

        var sorted = SftpSession.Sorted([File("b.txt"), Folder("zeta"), File("A.txt"), Folder("Alpha")]).Select(i => i.Name);

        Assert.Equal(["Alpha", "zeta", "A.txt", "b.txt"], sorted);
    }

    [Fact]
    public void Local_size_adds_up_files_and_folders()
    {
        var root = Directory.CreateTempSubdirectory("orrery-sftp-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[100]);
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllBytes(Path.Combine(root, "sub", "b.bin"), new byte[50]);

            Assert.Equal(150, SftpSession.LocalSize([Path.Combine(root, "a.bin"), Path.Combine(root, "sub")]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_server_that_cant_be_reached_fails_the_session_without_asking_for_a_login()
    {
        var session = SftpSession.Start(new ConnectionTarget("127.0.0.1", ClosedPort(), "me", null), new SshLogin("me", "pw"), Validator());
        var message = await FailureAsync(session);

        Assert.False(string.IsNullOrEmpty(message));
        Assert.False(session.AuthenticationRejected);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_jump_host_that_cant_be_reached_fails_the_session_naming_it()
    {
        var jump = new SshHop(new ConnectionTarget("127.0.0.1", ClosedPort(), "me", null), new SshLogin("me", "pw"));
        var session = SftpSession.Start(new ConnectionTarget("files.internal", 22, "me", null), new SshLogin("me", "pw"), Validator(), jumps: [jump]);

        Assert.StartsWith("Jump host 127.0.0.1", await FailureAsync(session));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Operations_on_a_session_that_isnt_connected_say_so()
    {
        var session = SftpSession.Start(new ConnectionTarget("127.0.0.1", ClosedPort(), "me", null), new SshLogin("me", "pw"), Validator());
        await FailureAsync(session);

        var e = await Assert.ThrowsAsync<SftpException>(() => session.ListAsync("/"));
        Assert.Equal("The connection is closed.", e.Message);
        await session.DisposeAsync();
    }

    HostKeyValidator Validator() => new(new JsonCertificateTrustStore(_path), _ => Task.FromResult(CertificateTrustDecision.Reject));

    static async Task<string?> FailureAsync(SftpSession session)
    {
        var failed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, e) =>
        {
            if (e.State == SessionState.Failed) failed.TrySetResult(e.Message);
        };
        if (session.State == SessionState.Failed) failed.TrySetResult("(failed before subscribing)");
        return await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() => File.Delete(_path);
}

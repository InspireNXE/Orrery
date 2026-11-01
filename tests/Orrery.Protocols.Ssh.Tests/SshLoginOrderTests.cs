using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Renci.SshNet;
using Renci.SshNet.Security;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

/// <summary>
/// The order SSH logins are tried in: a saved key, then the connection's key file, then the SSH agent's keys,
/// then the password. The agent is a fake, so nothing here reaches one.
/// </summary>
public class SshLoginOrderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-order-" + Guid.NewGuid().ToString("N"));

    public SshLoginOrderTests() => Directory.CreateDirectory(_dir);

    sealed class AgentKey : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = [];
    }

    static readonly AgentKey Agent1 = new(), Agent2 = new();

    static IPrivateKeySource[] FakeAgent() => [Agent1, Agent2];

    static IPrivateKeySource[] NoAgent() => [];

    string KeyFile(string text)
    {
        var path = Path.Combine(_dir, "id_" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Which key a source holds, by its public half: the first host algorithm's key blob.</summary>
    static string Blob(IPrivateKeySource key) => Convert.ToBase64String(key.HostKeyAlgorithms.First().Data);

    static string BlobOf(string pem)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(pem));
        return Blob(new PrivateKeyFile(stream));
    }

    static List<IPrivateKeySource> Keys(AuthenticationMethod[] methods) =>
        [.. methods.OfType<PrivateKeyAuthenticationMethod>().SelectMany(m => m.KeyFiles)];

    static string[] Names(AuthenticationMethod[] methods) => [.. methods.Select(m => m.Name)];

    [Fact]
    public void Everything_is_tried_in_order_saved_key_then_key_file_then_agent_then_password()
    {
        string saved = TestKeys.EcdsaPem(), file = TestKeys.RsaPem();
        var login = new SshLogin("me", "pw", PrivateKey: saved, UseAgent: true, KeyFile: KeyFile(file));

        var methods = SshSession.AuthenticationMethods(login, FakeAgent);

        // One public-key method that holds the keys in order, then the password ones.
        Assert.Equal(["publickey", "password", "keyboard-interactive"], Names(methods));
        var keys = Keys(methods);
        Assert.Equal(4, keys.Count);
        Assert.Equal(BlobOf(saved), Blob(keys[0]));
        Assert.Equal(BlobOf(file), Blob(keys[1]));
        Assert.Same(Agent1, keys[2]);
        Assert.Same(Agent2, keys[3]);
    }

    [Fact]
    public void The_key_file_comes_before_the_agent_even_with_no_saved_key()
    {
        var file = TestKeys.RsaPem();

        var keys = Keys(SshSession.AuthenticationMethods(new SshLogin("me", null, UseAgent: true, KeyFile: KeyFile(file)), FakeAgent));

        Assert.Equal(3, keys.Count);
        Assert.Equal(BlobOf(file), Blob(keys[0]));
        Assert.Same(Agent1, keys[1]);
    }

    [Fact]
    public void A_saved_key_comes_before_the_key_file()
    {
        string saved = TestKeys.EcdsaPem(), file = TestKeys.RsaPem();

        var keys = Keys(SshSession.AuthenticationMethods(new SshLogin("me", null, PrivateKey: saved, KeyFile: KeyFile(file)), NoAgent));

        Assert.Equal([BlobOf(saved), BlobOf(file)], keys.Select(Blob));
    }

    [Fact]
    public void The_password_is_last_and_only_when_there_is_one()
    {
        var withPassword = SshSession.AuthenticationMethods(new SshLogin("me", "pw", KeyFile: KeyFile(TestKeys.RsaPem())), NoAgent);
        var without = SshSession.AuthenticationMethods(new SshLogin("me", null, KeyFile: KeyFile(TestKeys.RsaPem())), NoAgent);

        Assert.Equal(["publickey", "password", "keyboard-interactive"], Names(withPassword));
        Assert.Equal(["publickey"], Names(without));
    }

    [Fact]
    public void A_password_login_is_unchanged()
    {
        var methods = SshSession.AuthenticationMethods(new SshLogin("me", "pw"), FakeAgent);

        Assert.Equal(["password", "keyboard-interactive"], Names(methods));
    }

    [Fact]
    public void The_agent_is_only_asked_when_the_login_allows_it()
    {
        bool asked = false;
        IPrivateKeySource[] Spy()
        {
            asked = true;
            return [Agent1];
        }

        SshSession.AuthenticationMethods(new SshLogin("me", "pw", KeyFile: KeyFile(TestKeys.RsaPem()), UseAgent: false), Spy);

        Assert.False(asked);
    }

    [Fact]
    public void An_agent_login_with_a_key_file_still_tries_the_file_when_the_agent_has_no_keys()
    {
        var file = TestKeys.RsaPem();

        var keys = Keys(SshSession.AuthenticationMethods(new SshLogin("me", null, UseAgent: true, KeyFile: KeyFile(file)), NoAgent));

        Assert.Equal([BlobOf(file)], keys.Select(Blob));
    }

    [Fact]
    public void Nothing_to_try_is_a_login_failure_not_a_crash()
    {
        Assert.Throws<NoLoginMethodException>(() => SshSession.AuthenticationMethods(new SshLogin("me", null, UseAgent: true), NoAgent));
    }

    [Fact]
    public void A_key_file_that_cant_be_used_stops_the_login_with_a_key_file_error()
    {
        var login = new SshLogin("me", "pw", KeyFile: Path.Combine(_dir, "gone"), UseAgent: true);

        var e = Assert.Throws<KeyFileException>(() => SshSession.AuthenticationMethods(login, FakeAgent));

        Assert.Equal(KeyFileProblem.Missing, e.Problem);
    }

    [Fact]
    public void An_encrypted_key_file_is_opened_with_the_passphrase_given()
    {
        var path = KeyFile(TestKeys.EncryptedRsaPem("open sesame"));

        Assert.Throws<KeyFileException>(() => SshSession.AuthenticationMethods(new SshLogin("me", null, KeyFile: path), NoAgent));
        Assert.Throws<KeyFileException>(() => SshSession.AuthenticationMethods(new SshLogin("me", null, KeyFile: path, KeyFilePassphrase: "wrong"), NoAgent));
        var keys = Keys(SshSession.AuthenticationMethods(new SshLogin("me", null, KeyFile: path, KeyFilePassphrase: "open sesame"), NoAgent));
        Assert.Single(keys);
    }

    [Fact]
    public void The_key_file_is_read_each_time_so_a_changed_file_is_seen_on_reconnect()
    {
        var path = KeyFile(TestKeys.RsaPem());
        var login = new SshLogin("me", null, KeyFile: path);
        var first = Blob(Keys(SshSession.AuthenticationMethods(login, NoAgent)).Single());

        File.WriteAllText(path, TestKeys.OtherRsaPem());
        var second = Blob(Keys(SshSession.AuthenticationMethods(login, NoAgent)).Single());

        Assert.NotEqual(first, second);
        Assert.Equal(BlobOf(TestKeys.OtherRsaPem()), second);
    }

    [Fact]
    public void A_refusal_says_what_was_tried()
    {
        var refused = new Renci.SshNet.Common.SshAuthenticationException("Permission denied.");

        Assert.Equal("The username or password was rejected.", SshSession.Refusal(refused, new SshLogin("me", "pw")));
        Assert.Equal("The server didn't accept the key file.", SshSession.Refusal(refused, new SshLogin("me", null, KeyFile: "~/k")));
        Assert.Equal("The server didn't accept the key.", SshSession.Refusal(refused, new SshLogin("me", null, PrivateKey: "k")));
        Assert.Equal("The server didn't accept any of the SSH agent's keys.", SshSession.Refusal(refused, new SshLogin("me", null, UseAgent: true)));
        Assert.Equal("The server didn't accept any of the keys that were tried.", SshSession.Refusal(refused, new SshLogin("me", null, UseAgent: true, KeyFile: "~/k")));
        Assert.Equal("The server didn't accept the key or the password.", SshSession.Refusal(refused, new SshLogin("me", "pw", KeyFile: "~/k")));
    }

    [Fact]
    public void A_logins_text_form_never_shows_its_secrets_or_the_key_files_path()
    {
        var text = new SshLogin("me", "pw-secret", "key-secret", "phrase-secret", true, "/home/me/private/id_x", "filephrase-secret").ToString();

        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("private", text);
        Assert.Contains("me", text);
    }

    // ---- Through the real session classes, which share the login path ----

    HostKeyValidator NoPrompt() => new(new MemoryTrustStore(), _ => Task.FromResult(CertificateTrustDecision.Reject));

    static readonly ConnectionTarget Box = new("127.0.0.1", 1, "me", null);

    [Fact]
    public async Task A_missing_key_file_fails_the_session_before_any_connection_is_made_and_is_a_key_file_failure()
    {
        var path = Path.Combine(_dir, "id_gone");
        // The port is closed, so a connection attempt would be "refused"; the key file is checked first.
        var (message, error) = await RetryingFailureOf(() => SshSession.Start(Box, new SshLogin("me", null, KeyFile: path), NoPrompt(), 80, 24));

        var failure = ConnectionFailure.Classify(error, message, Box);
        Assert.Equal(FailureKind.KeyFile, failure.Kind);
        Assert.True(failure.NeedsUser);
        Assert.False(failure.CanRetryByItself(dropped: true));
        Assert.Equal("Couldn't find the key file id_gone.", failure.Summary);
        Assert.Contains("key file", failure.Advice);
        Assert.DoesNotContain(_dir, failure.Summary + failure.Advice + failure.Detail);
    }

    [Fact]
    public async Task The_file_browser_fails_the_same_way()
    {
        var path = Path.Combine(_dir, "id_gone");
        var (message, error) = await RetryingFailureOf(() => SftpSession.Start(Box, new SshLogin("me", null, KeyFile: path), NoPrompt()));

        Assert.Equal(FailureKind.KeyFile, ConnectionFailure.Classify(error, message, Box).Kind);
    }

    [Fact]
    public async Task A_jump_host_with_a_bad_key_file_is_named_as_the_hop()
    {
        var hop = new SshHop(new ConnectionTarget("127.0.0.1", 1, "me", null), new SshLogin("me", null, KeyFile: Path.Combine(_dir, "id_gone")));
        var (message, error) = await RetryingFailureOf(() => SshSession.Start(new ConnectionTarget("db.internal", 22, "me", null), new SshLogin("me", "pw"), NoPrompt(), 80, 24, jumps: [hop]));

        var failure = ConnectionFailure.Classify(error, message, Box);
        Assert.Equal(FailureKind.JumpHost, failure.Kind);
        Assert.Equal(FailureKind.KeyFile, failure.Cause);
        Assert.True(failure.NeedsUser);
        Assert.DoesNotContain(_dir, failure.Summary + failure.Advice + failure.Detail);
    }

    [Fact]
    public async Task An_encrypted_key_file_without_its_passphrase_asks_for_it_rather_than_trying_the_server()
    {
        var path = KeyFile(TestKeys.EncryptedRsaPem("open sesame"));
        var (message, error) = await RetryingFailureOf(() => SshSession.Start(Box, new SshLogin("me", null, KeyFile: path), NoPrompt(), 80, 24));

        var failure = ConnectionFailure.Classify(error, message, Box);
        Assert.Equal(FailureKind.KeyFile, failure.Kind);
        Assert.Contains("passphrase", failure.Summary);
    }

    /// <summary>The error a session fails with. Started again if it failed before the handler was on, which loses the error.</summary>
    static async Task<(string? Message, Exception? Error)> RetryingFailureOf(Func<IConnectionSession> start)
    {
        for (int attempt = 0; ; attempt++)
        {
            var session = start();
            var failed = new TaskCompletionSource<(string?, Exception?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.StateChanged += (_, e) =>
            {
                if (e.State == SessionState.Failed) failed.TrySetResult((e.Message, e.Error));
            };
            try
            {
                if (session.State != SessionState.Failed || attempt == 5)
                {
                    return await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
    }

    sealed class MemoryTrustStore : ICertificateTrustStore
    {
        readonly Dictionary<(string, int), PinnedCertificate> _pins = [];
        public PinnedCertificate? Get(string host, int port) => _pins.GetValueOrDefault((host, port));
        public void Pin(string host, int port, PinnedCertificate certificate) => _pins[(host, port)] = certificate;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

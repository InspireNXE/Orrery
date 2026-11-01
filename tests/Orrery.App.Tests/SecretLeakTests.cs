using System.Text;
using Orrery.App.Logging;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Orrery.Protocols.Ssh;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>
/// Plants recognisable secrets, runs the flows that handle them with the real log running, and checks none of
/// them reach the log file.
/// </summary>
[Collection(nameof(LoggingTests))]
public class SecretLeakTests : IDisposable
{
    const string MasterPassword = "leak-master-7Q2x";
    const string NewMasterPassword = "leak-newmaster-9Lp4";
    const string WrongPassword = "leak-wrong-3Rv8";
    const string SavedPassword = "leak-saved-K5w1";
    const string KeyPassphrase = "leak-phrase-Z6m3";
    const string KeyBody = "leakkeybodyB4n7";
    const string ExportPassword = "leak-export-H8j2";
    const string TypedPassword = "leak-typed-D1s9";
    const string ProxySecret = "leak-proxy-W3t6";
    const string ConfigKeyBody = "leakconfigkeyP8c2";

    static readonly string[] Secrets = [MasterPassword, NewMasterPassword, WrongPassword, SavedPassword, KeyPassphrase, KeyBody, ExportPassword, TypedPassword, ProxySecret, ConfigKeyBody];

    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Passwords_keys_and_passphrases_never_reach_the_log()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        Directory.CreateDirectory(_root);
        var password = new Credential(Guid.NewGuid(), "Admin", CredentialKind.Password, "admin", "CORP", SavedPassword);
        var key = new Credential(Guid.NewGuid(), "Deploy key", CredentialKind.SshKeyWithPassphrase, "deploy", null,
            $"-----BEGIN OPENSSH PRIVATE KEY-----\n{KeyBody}\n-----END OPENSSH PRIVATE KEY-----", Passphrase: KeyPassphrase);

        var hello = new TestAuthenticator();
        var biometric = new BiometricUnlock(hello, new MemorySecrets());
        var dataKeys = new List<byte[]>();

        AppLog.Start(location);
        AppLog.Detailed = true;
        try
        {
            // The vault: created, a wrong password, unlocked, saved to, password changed, locked.
            var vaultPath = location.FileIn("vault.frv");
            using (var vault = new VaultFileStore(vaultPath, new VaultFileStore.KdfSettings(8 * 1024, 1)))
            {
                await vault.UnlockAsync(MasterPassword);
                await vault.SaveAllAsync([password, key]);
                vault.Lock();
                await Assert.ThrowsAsync<WrongVaultPasswordException>(() => vault.UnlockAsync(WrongPassword));
                await vault.UnlockAsync(MasterPassword);
                vault.Lock();

                // Biometric unlock: turned on (after a wrong password), used, then turned off by the password change.
                await Assert.ThrowsAsync<WrongVaultPasswordException>(() => biometric.TurnOnAsync(vault, WrongPassword));
                await biometric.TurnOnAsync(vault, MasterPassword);
                Assert.Equal(BiometricOutcome.Done, (await biometric.UnlockAsync(vault)).Outcome);
                dataKeys.Add((await vault.DataKeyForAsync(MasterPassword)).DataKey);
                await vault.ChangePasswordAsync(MasterPassword, NewMasterPassword);
                vault.Lock();
                Assert.Equal(BiometricOutcome.TurnedOff, (await biometric.UnlockAsync(vault)).Outcome);
                dataKeys.Add((await vault.DataKeyForAsync(NewMasterPassword)).DataKey);
            }

            // Connections saved, backed up and loaded.
            var tree = ConnectionTree.Empty();
            var db = new ConnectionEntry { Name = "db", Host = "db.internal", Settings = { Protocol = Protocol.Ssh, CredentialId = key.Id } };
            tree.Add(db, tree.Root);
            tree.Add(new ConnectionEntry { Name = "web", Host = "web.example", Settings = { CredentialId = password.Id } }, tree.Root);
            var store = new ConnectionStore(location.FileIn("connections.json"), location.FileIn("backups"), backupInterval: TimeSpan.Zero);
            store.Save(tree);
            store.Save(tree);
            store.Load();

            // An export with passwords, read back in.
            var credentials = new Dictionary<Guid, Credential> { [password.Id] = password, [key.Id] = key };
            var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, credentials.ToDictionary(c => c.Key, c => c.Value.Summary),
                DateTimeOffset.Now, new ExportSecrets(ExportPassword, credentials));
            OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "export.json", ExportPassword);
            Assert.ThrowsAny<Exception>(() => OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "export.json", WrongPassword));

            // An OpenSSH config with secrets in its commands and in the key file it names, imported.
            var keyFile = Path.Combine(_root, "id_leak");
            File.WriteAllText(keyFile, $"-----BEGIN OPENSSH PRIVATE KEY-----\n{ConfigKeyBody}\n-----END OPENSSH PRIVATE KEY-----\n");
            var sshConfig = Path.Combine(_root, "config");
            File.WriteAllText(sshConfig, $"Host leaky\n  HostName leaky.example.com\n  IdentityFile {keyFile}\n  ProxyCommand nc -x {ProxySecret} %h %p\n  LocalCommand echo {ProxySecret}\n");
            Assert.Single(SshConfigImporter.ImportFile(sshConfig).Root.Descendants());

            // SSH connections that fail, with a typed password and with a key, directly and through a jump host.
            var hostKeys = new HostKeyValidator(new JsonCertificateTrustStore(location.FileIn("known-hosts.json")),
                _ => Task.FromResult(CertificateTrustDecision.Reject));
            int closed = ClosedPort();
            await RunToEnd(SshSession.Start(new ConnectionTarget("127.0.0.1", closed, "admin", null), new SshLogin("admin", TypedPassword), hostKeys, 80, 24));
            await RunToEnd(SshSession.Start(new ConnectionTarget("db.internal", 22, "deploy", null), new SshLogin("deploy", null, key.Secret, KeyPassphrase),
                hostKeys, 80, 24, jumps: [new SshHop(new ConnectionTarget("127.0.0.1", closed, "admin", null), new SshLogin("admin", TypedPassword))]));
        }
        finally
        {
            AppLog.Detailed = false;
            AppLog.Stop();
        }

        var log = string.Join("\n", Directory.GetFiles(location.LogDir).Select(File.ReadAllText));
        Assert.Contains("Vault unlock failed", log);
        Assert.Contains("Backed up connections", log);
        Assert.Contains("Read SSH config", log);
        Assert.Contains("127.0.0.1", log);
        Assert.Contains("Vault unlocked with Test Hello", log);
        Assert.Contains("Test Hello unlock turned off", log);
        string details;
        lock (FailureDetails) details = string.Join("\n", FailureDetails);
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, log);
            Assert.DoesNotContain(secret, details);
        }

        // The authenticator's secrets, and the vault's data keys before and after the password change.
        Assert.NotEmpty(hello.Secrets);
        Assert.Equal(2, dataKeys.Count);
        foreach (var secret in hello.Secrets.Concat(dataKeys))
        {
            Assert.DoesNotContain(Convert.ToBase64String(secret), log);
            Assert.DoesNotContain(Convert.ToHexString(secret), log, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>What the connection-lost screen showed and would copy for the failed sessions.</summary>
    static readonly List<string> FailureDetails = [];

    static async Task RunToEnd(SshSession session)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, e) =>
        {
            if (e.State == SessionState.Failed)
            {
                var failure = ConnectionFailure.Classify(e.Error, e.Message, session.Target);
                lock (FailureDetails) FailureDetails.Add($"{failure.Summary} {failure.Advice} {failure.Detail}");
            }

            if (e.State is SessionState.Failed or SessionState.Disconnected) ended.TrySetResult();
        };
        if (session.State is SessionState.Failed or SessionState.Disconnected) ended.TrySetResult();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await session.DisposeAsync();
    }

    static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

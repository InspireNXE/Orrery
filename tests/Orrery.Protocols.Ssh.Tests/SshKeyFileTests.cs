using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

/// <summary>Reading an SSH key file: what's refused, what's asked for, and what the messages say.</summary>
public class SshKeyFileTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-keyfile-" + Guid.NewGuid().ToString("N"));

    public SshKeyFileTests() => Directory.CreateDirectory(_dir);

    string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    KeyFileException Fails(string path, string? passphrase = null)
    {
        var e = Assert.Throws<KeyFileException>(() => SshKeyFile.Load(path, passphrase).Dispose());

        // The message goes in the log and on the connection-lost screen: the file's name, never its folder.
        Assert.DoesNotContain(_dir, e.Message);
        Assert.Null(e.InnerException);
        return e;
    }

    [Fact]
    public void A_plain_key_loads()
    {
        var path = Write("id_test", TestKeys.RsaPem());

        using var key = SshKeyFile.Load(path);

        Assert.Contains("rsa", key.HostKeyAlgorithms.First().Name);
        Assert.Null(SshKeyFile.Check(path));
    }

    [Fact]
    public void A_missing_file_is_a_plain_error_naming_the_file_only()
    {
        var e = Fails(Path.Combine(_dir, "nothing_here"));

        Assert.Equal(KeyFileProblem.Missing, e.Problem);
        Assert.Equal("Couldn't find the key file nothing_here.", e.Message);
    }

    [Fact]
    public void A_missing_folder_in_the_path_is_the_same_error()
    {
        Assert.Equal(KeyFileProblem.Missing, Fails(Path.Combine(_dir, "no", "such", "folder", "id")).Problem);
    }

    [Fact]
    public void A_folder_is_not_a_key_file()
    {
        var folder = Path.Combine(_dir, "keys");
        Directory.CreateDirectory(folder);

        var e = Fails(folder);

        Assert.Equal(KeyFileProblem.NotAFile, e.Problem);
        Assert.Equal("keys is a folder, not a key file.", e.Message);
    }

    [Fact]
    public void A_file_over_the_size_cap_is_refused_without_being_read_into_memory_whole()
    {
        var path = Path.Combine(_dir, "huge");
        File.WriteAllBytes(path, new byte[SshKeyFile.MaxBytes + 1]);

        var e = Fails(path);

        Assert.Equal(KeyFileProblem.TooBig, e.Problem);
        Assert.Contains("huge is too big", e.Message);
    }

    [Fact]
    public void A_file_just_under_the_cap_is_read_and_judged_by_what_is_in_it()
    {
        var path = Path.Combine(_dir, "padded");
        File.WriteAllBytes(path, new byte[SshKeyFile.MaxBytes]);

        Assert.Equal(KeyFileProblem.NotAKey, Fails(path).Problem);
    }

    [Fact]
    public void A_file_that_cant_be_opened_is_unreadable_not_a_crash()
    {
        var path = Write("locked", TestKeys.RsaPem());
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // On Windows an exclusive lock makes the open fail; elsewhere locks are advisory and it loads.
        if (OperatingSystem.IsWindows())
        {
            var e = Fails(path);
            Assert.Equal(KeyFileProblem.Unreadable, e.Problem);
            Assert.Equal("Couldn't read the key file locked. Check that your account is allowed to open it.", e.Message);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_path_is_said_plainly(string path)
    {
        var e = Assert.Throws<KeyFileException>(() => SshKeyFile.Load(path));
        Assert.Equal(KeyFileProblem.BadPath, e.Problem);
        Assert.Equal("No key file is set.", e.Message);
    }

    [Fact]
    public void A_relative_path_is_refused_rather_than_looked_for_in_whatever_folder_orrery_runs_in()
    {
        var e = Assert.Throws<KeyFileException>(() => SshKeyFile.Load("keys/id_rsa"));
        Assert.Equal(KeyFileProblem.BadPath, e.Problem);
        Assert.Contains("id_rsa", e.Message);
        Assert.DoesNotContain("keys/", e.Message);
    }

    [Fact]
    public void An_encrypted_key_asks_for_its_passphrase()
    {
        var path = Write("id_locked", TestKeys.EncryptedRsaPem("correct horse"));

        var e = Fails(path);

        Assert.Equal(KeyFileProblem.NeedsPassphrase, e.Problem);
        Assert.Equal("The key file id_locked is protected by a passphrase.", e.Message);
    }

    [Fact]
    public void A_wrong_passphrase_is_told_apart_from_a_missing_one()
    {
        var path = Write("id_locked", TestKeys.EncryptedRsaPem("correct horse"));

        var e = Fails(path, "battery staple");

        Assert.Equal(KeyFileProblem.WrongPassphrase, e.Problem);
        Assert.Contains("id_locked", e.Message);
        Assert.DoesNotContain("battery staple", e.Message);
    }

    [Fact]
    public void The_right_passphrase_opens_an_encrypted_key()
    {
        var path = Write("id_locked", TestKeys.EncryptedRsaPem("correct horse"));

        using var key = SshKeyFile.Load(path, "correct horse");

        Assert.NotEmpty(key.HostKeyAlgorithms);
        Assert.Null(SshKeyFile.Check(path, "correct horse"));
        Assert.Equal(KeyFileProblem.WrongPassphrase, SshKeyFile.Check(path, "nope")!.Problem);
    }

    [Fact]
    public void A_passphrase_given_for_a_key_that_has_none_is_ignored()
    {
        var path = Write("id_plain", TestKeys.RsaPem());

        Assert.Null(SshKeyFile.Check(path, "not needed"));
    }

    [Fact]
    public void Something_that_looks_like_a_key_but_isnt_a_usable_one_is_unsupported()
    {
        var path = Write("id_odd", "-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----\n");

        var e = Fails(path);

        Assert.Equal(KeyFileProblem.Unsupported, e.Problem);
        Assert.Contains("id_odd", e.Message);
        Assert.Contains("isn't supported", e.Message);
    }

    [Fact]
    public void A_file_that_isnt_a_key_at_all_says_so()
    {
        var path = Write("notes.txt", "shopping list\nmilk\n");

        var e = Fails(path);

        Assert.Equal(KeyFileProblem.NotAKey, e.Problem);
        Assert.Equal("notes.txt isn't an SSH private key.", e.Message);
    }

    [Fact]
    public void A_public_key_is_recognised_and_the_user_is_pointed_at_the_private_one()
    {
        var path = Write("id_ed25519.pub", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOnotarealkeyatallbutshapedlikeone me@host\n");

        var e = Fails(path);

        Assert.Equal(KeyFileProblem.NotAKey, e.Problem);
        Assert.Contains("is a public key", e.Message);
    }

    [Fact]
    public void Garbage_never_escapes_as_anything_but_a_key_file_error()
    {
        var random = new Random(7);
        for (int i = 0; i < 40; i++)
        {
            var junk = new byte[random.Next(1, 3000)];
            random.NextBytes(junk);
            var header = i % 2 == 0 ? "-----BEGIN OPENSSH PRIVATE KEY-----\n" : "-----BEGIN RSA PRIVATE KEY-----\n";
            var path = Path.Combine(_dir, $"junk{i}");
            File.WriteAllText(path, header + Convert.ToBase64String(junk) + "\n-----END PRIVATE KEY-----\n");

            Assert.NotNull(SshKeyFile.Check(path));
            Assert.NotNull(SshKeyFile.Check(path, "phrase"));
        }
    }

    [Fact]
    public void A_leading_tilde_is_the_home_folder_and_nothing_else_is_expanded()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(home, SshKeyFile.Expand("~"));
        Assert.Equal(Path.Combine(home, ".ssh", "id_ed25519"), SshKeyFile.Expand("~/.ssh/id_ed25519"));
        Assert.Equal(Path.Combine(home, "keys", "id"), SshKeyFile.Expand(OperatingSystem.IsWindows() ? "~\\keys\\id" : "~/keys/id"));
        Assert.Equal("~\\keys", OperatingSystem.IsWindows() ? "~\\keys" : SshKeyFile.Expand("~\\keys"));
        Assert.Equal("~someone/id", SshKeyFile.Expand("~someone/id"));
        Assert.Equal("/etc/~/id", SshKeyFile.Expand("/etc/~/id"));
        Assert.Equal("$HOME/id", SshKeyFile.Expand("$HOME/id"));
    }

    [Fact]
    public void A_tilde_path_that_isnt_there_is_missing_with_only_its_name_in_the_message()
    {
        var e = Assert.Throws<KeyFileException>(() => SshKeyFile.Load("~/orrery-test-" + Guid.NewGuid().ToString("N") + "/id_none"));

        Assert.Equal(KeyFileProblem.Missing, e.Problem);
        Assert.Equal("Couldn't find the key file id_none.", e.Message);
    }

    [Fact]
    public void Messages_name_the_file_without_its_folder()
    {
        Assert.Equal("id_rsa", SshKeyFile.NameOf("~/.ssh/id_rsa"));
        Assert.Equal("id_rsa", SshKeyFile.NameOf(@"C:\Users\me\.ssh\id_rsa"));
        Assert.Equal("the key file", SshKeyFile.NameOf(""));
    }

    [Fact]
    public void A_passphrase_is_saved_under_an_id_that_comes_from_the_path_alone()
    {
        var a = SshKeyFile.PassphraseCredentialId(Path.Combine(_dir, "id_a"));

        Assert.Equal(a, SshKeyFile.PassphraseCredentialId(Path.Combine(_dir, "id_a")));
        Assert.Equal(a, SshKeyFile.PassphraseCredentialId(Path.Combine(_dir, "sub", "..", "id_a")));
        Assert.NotEqual(a, SshKeyFile.PassphraseCredentialId(Path.Combine(_dir, "id_b")));
    }

    [Fact]
    public void A_key_other_users_can_read_gets_a_warning_but_still_loads()
    {
        if (OperatingSystem.IsWindows())
        {
            // No Unix modes here.
            Assert.Null(SshKeyFile.PermissionWarning(Write("id_win", TestKeys.RsaPem())));
            return;
        }

        var path = Write("id_open", TestKeys.RsaPem());
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var warning = SshKeyFile.PermissionWarning(path);

        Assert.NotNull(warning);
        Assert.Contains("id_open", warning);
        Assert.Contains("644", warning);
        Assert.DoesNotContain(_dir, warning);
        Assert.Null(SshKeyFile.Check(path));

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(SshKeyFile.PermissionWarning(path));
    }

    [Fact]
    public void A_missing_file_has_no_permission_warning()
    {
        Assert.Null(SshKeyFile.PermissionWarning(Path.Combine(_dir, "gone")));
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

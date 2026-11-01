using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Orrery.Protocols.Ssh;

/// <summary>Why a key file couldn't be used.</summary>
public enum KeyFileProblem
{
    /// <summary>No path, or one that isn't absolute and doesn't start with ~.</summary>
    BadPath,
    Missing,
    /// <summary>A folder, a device or anything else that isn't an ordinary file.</summary>
    NotAFile,
    TooBig,
    Unreadable,
    NeedsPassphrase,
    WrongPassphrase,
    /// <summary>Not a private key at all, such as a public key or some other file.</summary>
    NotAKey,
    /// <summary>Looks like a private key, in a format or of a type Orrery's SSH library doesn't handle.</summary>
    Unsupported,
}

/// <summary>
/// A key file Orrery couldn't use. The message is plain, and names the file but never its folder, since it ends up
/// in the log and on the connection-lost screen. It carries no inner exception for the same reason: the system's
/// own file errors quote the whole path.
/// </summary>
public sealed class KeyFileException(KeyFileProblem problem, string message) : SshException(message)
{
    public KeyFileProblem Problem { get; } = problem;
}

/// <summary>
/// Reads an SSH private key from a file on this computer, at connect time. The file is read, checked and parsed;
/// it's never copied anywhere, and neither its contents nor its folder are logged or put in a message.
/// </summary>
public static class SshKeyFile
{
    /// <summary>Private keys are a few kilobytes; even a 16384-bit RSA key in PEM is about 13.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The path with a leading ~ (this user's home folder) replaced. Nothing else is expanded.</summary>
    public static string Expand(string path)
    {
        path = path.Trim();
        // A backslash is a separator only on Windows; elsewhere it is part of a name.
        bool windows = Path.DirectorySeparatorChar == '\\';
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || windows && path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0)
            {
                var rest = path.Length > 2 ? path[2..].TrimStart('/', '\\') : string.Empty;
                return rest.Length == 0 ? home : Path.Combine(home, windows ? rest.Replace('/', '\\') : rest);
            }
        }

        return path;
    }

    /// <summary>Just the file's name, for messages and the log.</summary>
    public static string NameOf(string path)
    {
        var name = Path.GetFileName(Expand(path).TrimEnd('/', '\\'));
        return name.Length == 0 ? "the key file" : name;
    }

    /// <summary>
    /// Reads and parses the key in <paramref name="path"/>, with <paramref name="passphrase"/> if it has one.
    /// </summary>
    /// <exception cref="KeyFileException">The file is missing, isn't an ordinary file, is too big or unreadable, isn't a
    /// usable private key, or needs a passphrase (<see cref="KeyFileProblem.NeedsPassphrase"/>) or has a different one
    /// (<see cref="KeyFileProblem.WrongPassphrase"/>).</exception>
    public static PrivateKeyFile Load(string path, string? passphrase = null)
    {
        var bytes = ReadBytes(path);
        try
        {
            return Parse(bytes, NameOf(path), passphrase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Like <see cref="Load"/>, but returns what went wrong instead of throwing. The key is disposed.</summary>
    public static KeyFileException? Check(string path, string? passphrase = null)
    {
        try
        {
            Load(path, passphrase).Dispose();
            return null;
        }
        catch (KeyFileException e)
        {
            return e;
        }
    }

    /// <summary>Parses a key held in memory, naming it <paramref name="name"/> in messages.</summary>
    internal static PrivateKeyFile Parse(byte[] bytes, string name, string? passphrase)
    {
        bool encrypted = LooksEncrypted(bytes);
        if (encrypted && string.IsNullOrEmpty(passphrase))
        {
            throw new KeyFileException(KeyFileProblem.NeedsPassphrase, $"The key file {name} is protected by a passphrase.");
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            throw new KeyFileException(KeyFileProblem.NeedsPassphrase, $"The key file {name} is protected by a passphrase.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever the library throws at a file that isn't what it expects (it isn't picky about the type), this
            // is the user's file, not a fault in Orrery. The exception isn't kept: its text can quote the key.
            if (encrypted)
            {
                throw new KeyFileException(KeyFileProblem.WrongPassphrase, $"Couldn't open the key file {name} with that passphrase. Check the passphrase; if it is right, the key's format may not be supported.");
            }

            if (IsPublicKey(bytes, name))
            {
                throw new KeyFileException(KeyFileProblem.NotAKey, $"{name} is a public key. Choose the private key, the one without .pub.");
            }

            throw LooksLikeKey(bytes)
                ? new KeyFileException(KeyFileProblem.Unsupported, $"Orrery can't use the key file {name}: its format or key type isn't supported. OpenSSH, PEM and PuTTY (.ppk) keys of RSA, ECDSA and Ed25519 work.")
                : new KeyFileException(KeyFileProblem.NotAKey, $"{name} isn't an SSH private key.");
        }
    }

    /// <summary>The file's bytes, after checking that it is an ordinary file small enough to be a key.</summary>
    static byte[] ReadBytes(string path)
    {
        string name = NameOf(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new KeyFileException(KeyFileProblem.BadPath, "No key file is set.");
        }

        var full = Expand(path);
        if (!Path.IsPathRooted(full) || full.IndexOf('\0') >= 0)
        {
            throw new KeyFileException(KeyFileProblem.BadPath, $"The key file path for {name} must be a full path, or start with ~ for your home folder.");
        }

        try
        {
            if (Directory.Exists(full))
            {
                throw new KeyFileException(KeyFileProblem.NotAFile, $"{name} is a folder, not a key file.");
            }

            if (!File.Exists(full))
            {
                throw new KeyFileException(KeyFileProblem.Missing, $"Couldn't find the key file {name}.");
            }

            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);

            // A pipe or device has no length to check, and could go on for ever.
            if (!stream.CanSeek)
            {
                throw new KeyFileException(KeyFileProblem.NotAFile, $"{name} isn't an ordinary file.");
            }

            if (stream.Length > MaxBytes)
            {
                throw new KeyFileException(KeyFileProblem.TooBig, $"{name} is too big to be a key file (more than {MaxBytes / 1024} KB).");
            }

            var buffer = new byte[MaxBytes + 1];
            int total = 0, read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            if (total > MaxBytes)
            {
                CryptographicOperations.ZeroMemory(buffer);
                throw new KeyFileException(KeyFileProblem.TooBig, $"{name} is too big to be a key file (more than {MaxBytes / 1024} KB).");
            }

            var bytes = buffer.AsSpan(0, total).ToArray();
            CryptographicOperations.ZeroMemory(buffer);
            return bytes;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new KeyFileException(KeyFileProblem.Missing, $"Couldn't find the key file {name}.");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException or ArgumentException)
        {
            throw new KeyFileException(KeyFileProblem.Unreadable, $"Couldn't read the key file {name}. Check that your account is allowed to open it.");
        }
    }

    /// <summary>
    /// A warning when other users on this computer can read the file, which OpenSSH itself refuses to use; null
    /// otherwise, and always null on Windows, where files don't have these modes. Never refuses.
    /// </summary>
    public static string? PermissionWarning(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var full = Expand(path);
            var mode = File.GetUnixFileMode(full);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0)
            {
                return null;
            }

            return $"The key file {NameOf(path)} can be read by other users on this computer (mode {Convert.ToString((int)mode & 0x1FF, 8).PadLeft(3, '0')}). "
                + "OpenSSH would refuse it; run chmod 600 on it to keep the key private.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The credential id a key file's passphrase is saved under, so the same file finds it again whichever
    /// connection uses it. It comes from the full path, which isn't stored anywhere.
    /// </summary>
    public static Guid PassphraseCredentialId(string path)
    {
        var full = Expand(path);
        try
        {
            full = Path.GetFullPath(full);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        if (OperatingSystem.IsWindows())
        {
            full = full.ToUpperInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("orrery key file passphrase:" + full));
        return new Guid(hash.AsSpan(0, 16));
    }

    // ---- Recognizing what a file is, without trusting the library's messages ----

    static string Head(byte[] bytes) => Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));

    static bool LooksLikeKey(byte[] bytes)
    {
        var head = Head(bytes);
        return head.Contains("PRIVATE KEY", StringComparison.Ordinal) || head.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal)
            || head.StartsWith("---- BEGIN SSH2 ENCRYPTED PRIVATE KEY", StringComparison.Ordinal);
    }

    static bool IsPublicKey(byte[] bytes, string name)
    {
        var head = Head(bytes).TrimStart();
        return head.Contains("PUBLIC KEY", StringComparison.Ordinal)
            || (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && !LooksLikeKey(bytes))
            || head.StartsWith("ssh-", StringComparison.Ordinal) || head.StartsWith("ecdsa-sha2-", StringComparison.Ordinal);
    }

    /// <summary>Whether the key is protected by a passphrase, for the formats that say so in plain sight.</summary>
    static bool LooksEncrypted(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal))
        {
            return OpenSshCipher(text) is { } cipher && cipher != "none";
        }

        if (text.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal) || text.Contains("DEK-Info:", StringComparison.Ordinal)
            || text.Contains("ENCRYPTED", StringComparison.Ordinal) && text.Contains("Proc-Type:", StringComparison.Ordinal)
            || text.StartsWith("---- BEGIN SSH2 ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("Encryption:", StringComparison.Ordinal))
                {
                    return !line["Encryption:".Length..].Trim().Equals("none", StringComparison.Ordinal);
                }
            }
        }

        return false;
    }

    /// <summary>The cipher name in an openssh-key-v1 file: "none" when the key isn't encrypted.</summary>
    static string? OpenSshCipher(string pem)
    {
        const string begin = "-----BEGIN OPENSSH PRIVATE KEY-----", end = "-----END OPENSSH PRIVATE KEY-----";
        int from = pem.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
        int to = pem.IndexOf(end, from, StringComparison.Ordinal);
        if (to < 0)
        {
            return null;
        }

        try
        {
            var body = Convert.FromBase64String(string.Concat(pem.AsSpan(from, to - from).ToString().Where(c => !char.IsWhiteSpace(c))));
            var magic = "openssh-key-v1\0"u8;
            if (body.Length < magic.Length + 4 || !body.AsSpan(0, magic.Length).SequenceEqual(magic))
            {
                return null;
            }

            int offset = magic.Length;
            int length = (body[offset] << 24) | (body[offset + 1] << 16) | (body[offset + 2] << 8) | body[offset + 3];
            offset += 4;
            return length is < 0 or > 64 || offset + length > body.Length ? null : Encoding.ASCII.GetString(body, offset, length);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Orrery.Core.Import;

/// <summary>
/// Reads values encrypted by mRemoteNG's current format (ConfVersion 2.6, <c>EncryptionEngine="AES"</c>,
/// <c>BlockCipherMode="GCM"</c>). Written from the file format, not from mRemoteNG's GPL code.
/// <para>
/// Each value is base64 of salt (16 bytes) + nonce (16 bytes) + ciphertext and 128-bit tag. The key is
/// PBKDF2-HMAC-SHA1 of the password over the salt, for <c>KdfIterations</c> rounds, 256 bits long; the
/// salt is also the GCM associated data. .NET's AesGcm only takes 12-byte nonces, hence BouncyCastle.
/// </para>
/// </summary>
public static class MRemoteNgCrypto
{
    /// <summary>The key mRemoteNG uses when the user hasn't set a master password.</summary>
    public const string DefaultPassword = "mR3m";

    /// <summary>The <c>Protected</c> marker decrypts to one of these.</summary>
    public const string NotProtectedMarker = "ThisIsNotProtected";
    public const string ProtectedMarker = "ThisIsProtected";

    const int SaltSize = 16;
    const int NonceSize = 16;
    const int MacBits = 128;
    const int KeyBits = 256;

    /// <returns>The plaintext, or null if the password is wrong or the value isn't in this format.</returns>
    public static string? TryDecrypt(string value, string password, int iterations) =>
        TryDecrypt(value, password, iterations, beforeKeyDerivation: null);

    /// <param name="beforeKeyDerivation">Called just before the costly PBKDF2 step, which a value that isn't
    /// in this format never reaches. The importer uses it to keep a budget; it may throw to stop.</param>
    internal static string? TryDecrypt(string value, string password, int iterations, Action? beforeKeyDerivation)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }

        if (data.Length < SaltSize + NonceSize + MacBits / 8)
        {
            return null;
        }

        var salt = data[..SaltSize];
        var nonce = data[SaltSize..(SaltSize + NonceSize)];
        var ciphertext = data[(SaltSize + NonceSize)..];

        beforeKeyDerivation?.Invoke();
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(DeriveKey(password, salt, iterations), MacBits, nonce, salt));
        var output = new byte[cipher.GetOutputSize(ciphertext.Length)];
        try
        {
            int length = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, output, 0);
            length += cipher.DoFinal(output, length);
            return Encoding.UTF8.GetString(output, 0, length);
        }
        catch (InvalidCipherTextException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    /// <summary>Encrypts in the same format. Used for the mRemoteNG export and by tests to build fixtures.</summary>
    public static string Encrypt(string plaintext, string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var input = Encoding.UTF8.GetBytes(plaintext);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(DeriveKey(password, salt, iterations), MacBits, nonce, salt));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        int length = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        length += cipher.DoFinal(output, length);

        return Convert.ToBase64String([.. salt, .. nonce, .. output.AsSpan(0, length)]);
    }

    static KeyParameter DeriveKey(string password, byte[] salt, int iterations)
    {
        var generator = new Pkcs5S2ParametersGenerator();
        generator.Init(PbeParametersGenerator.Pkcs5PasswordToUtf8Bytes(password.ToCharArray()), salt, iterations);
        return (KeyParameter)generator.GenerateDerivedMacParameters(KeyBits);
    }
}

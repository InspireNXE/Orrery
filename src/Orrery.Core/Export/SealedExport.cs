using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using Orrery.Core.Connections;

namespace Orrery.Core.Export;

/// <summary>
/// An Orrery export sealed with a password, for exports that include saved passwords and keys. The whole
/// export is encrypted, host names included.
/// <para>
/// Password → Argon2id → 256-bit key → XChaCha20-Poly1305 over the export's JSON, as the vault does
/// (<see cref="Credentials.VaultFileStore"/>), without the vault's wrapped data key: the file is written once
/// and never re-keyed. The format, version and KDF settings are bound in as associated data, so editing
/// them makes decryption fail instead of weakening the file.
/// </para>
/// </summary>
internal static class SealedExport
{
    internal const string FormatName = "orrery-connections-encrypted";
    const int Version = 1;
    const string KdfName = "argon2id";
    const string CipherName = "xchacha20poly1305";
    const int SaltSize = 16;
    const int KeySize = 32;

    // Argon2 limits accepted from a file, so a hostile one can't make Orrery spend minutes or gigabytes on it.
    const int MinMemoryKiB = 8 * 1024;
    const int MaxMemoryKiB = 1024 * 1024;
    const int MaxIterations = 100;

    static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    /// <summary>Argon2id cost for new files; the same as a new vault's.</summary>
    internal sealed record KdfSettings(int MemoryKiB, int Iterations)
    {
        public static readonly KdfSettings Default = new(MemoryKiB: 64 * 1024, Iterations: 3);
    }

    internal sealed record SealedFile(string Format, int Version, KdfBlock Kdf, CipherBlock Payload);

    internal sealed record KdfBlock(string Alg, int MemoryKiB, int Iterations, int Parallelism, string Salt);

    internal sealed record CipherBlock(string Alg, string Nonce, [property: JsonPropertyName("ct")] string Ciphertext);

    /// <summary>Encrypts <paramref name="plaintext"/> (an export's JSON) into a sealed file's text.</summary>
    internal static string Seal(byte[] plaintext, string password, KdfSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        settings ??= KdfSettings.Default;
        var kdf = new KdfBlock(KdfName, settings.MemoryKiB, settings.Iterations, 1, Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize)));
        using var key = DeriveKey(password, kdf);
        var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
        var ciphertext = Aead.Encrypt(key, nonce, AssociatedData(kdf), plaintext);
        var file = new SealedFile(FormatName, Version, kdf, new CipherBlock(CipherName, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext)));
        return JsonSerializer.Serialize(file, ConnectionStore.JsonOptions);
    }

    /// <summary>True if <paramref name="json"/> is a sealed export, going by its <c>format</c>.</summary>
    internal static bool IsSealed(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty("format", out var format)
        && format.ValueKind == JsonValueKind.String
        && format.GetString() == FormatName;

    /// <returns>The export's JSON, or null if <paramref name="password"/> is wrong.</returns>
    /// <exception cref="InvalidDataException">The file is damaged, newer than this version reads, or asks for too much work to open.</exception>
    internal static byte[]? Open(SealedFile file, string password)
    {
        if (file.Version > Version)
        {
            throw new InvalidDataException("This file was exported by a newer version of Orrery.");
        }

        bool valid = file is { Format: FormatName, Kdf: { Alg: KdfName } kdf, Payload: { Alg: CipherName } payload }
            && kdf.MemoryKiB is >= MinMemoryKiB and <= MaxMemoryKiB
            && kdf.Iterations is >= 1 and <= MaxIterations
            && kdf.Parallelism == 1
            && DecodedLength(kdf.Salt) == SaltSize
            && DecodedLength(payload.Nonce) == Aead.NonceSize
            && DecodedLength(payload.Ciphertext) >= Aead.TagSize;
        if (!valid)
        {
            throw new InvalidDataException("This encrypted export is damaged.");
        }

        using var key = DeriveKey(password, file.Kdf);
        return Aead.Decrypt(key, Convert.FromBase64String(file.Payload.Nonce), AssociatedData(file.Kdf), Convert.FromBase64String(file.Payload.Ciphertext));
    }

    static Key DeriveKey(string password, KdfBlock kdf)
    {
        var parameters = new Argon2Parameters
        {
            DegreeOfParallelism = kdf.Parallelism,
            MemorySize = kdf.MemoryKiB,
            NumberOfPasses = kdf.Iterations,
        };

        var bytes = PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters).DeriveBytes(password, Convert.FromBase64String(kdf.Salt), KeySize);
        try
        {
            return Key.Import(Aead, bytes, KeyBlobFormat.RawSymmetricKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    static byte[] AssociatedData(KdfBlock kdf) =>
        Encoding.UTF8.GetBytes($"{FormatName}|v{Version}|{kdf.Alg}|m={kdf.MemoryKiB}|t={kdf.Iterations}|p={kdf.Parallelism}|salt={kdf.Salt}");

    /// <summary>The byte length of a base64 string, or -1 if it is missing or not valid base64.</summary>
    static int DecodedLength(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return -1;
        }

        var buffer = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, buffer, out int written) ? written : -1;
    }
}

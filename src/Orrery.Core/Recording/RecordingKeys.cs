using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Orrery.Core.Credentials;

namespace Orrery.Core.Recording;

/// <summary>
/// The recordings key pair (docs/design-session-recording.md §3.3). Recordings are sealed to the public key, kept in
/// settings, so recording never needs the credential store open. The private key, which playing needs, is a hidden
/// credential (<see cref="CredentialId"/>) in whichever store the data folder uses.
/// <para>
/// A file key is sealed with a fresh X25519 key pair: the agreement between its private half and the recordings
/// public key, through HKDF-SHA256, gives the key that seals the file key with XChaCha20-Poly1305. Only the fresh
/// public half and the sealed key are kept.
/// </para>
/// </summary>
public static class RecordingKeys
{
    /// <summary>The hidden credential holding the private key. Never listed (<see cref="Credential.ReservedIds"/>).</summary>
    public static readonly Guid CredentialId = new("6f726572-7279-7265-636f-7264696e6773");

    public const int KeySize = 32;
    const int FileKeySize = 32;
    const string CredentialName = "Orrery recordings key";
    static readonly byte[] Info = Encoding.ASCII.GetBytes("orrery-recording|v1|file-key");
    static readonly KeyAgreementAlgorithm Agreement = KeyAgreementAlgorithm.X25519;
    static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    /// <summary>A short id for a public key, kept in each recording's header so a file sealed to another key is told apart.</summary>
    public static string KeyId(ReadOnlySpan<byte> publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey)[..8]);

    /// <summary>The private key in <paramref name="store"/>, or null when there isn't one. The store must be open.</summary>
    /// <exception cref="InvalidDataException">The saved key isn't a key.</exception>
    public static async Task<byte[]?> PrivateKeyAsync(ICredentialStore store)
    {
        if (await store.GetAsync(CredentialId) is not { } saved)
        {
            return null;
        }

        var key = new byte[KeySize];
        if (!Convert.TryFromBase64String(saved.Secret, key, out int written) || written != KeySize)
        {
            throw new InvalidDataException("The saved recordings key is damaged.");
        }

        return key;
    }

    /// <summary>
    /// The public key, making the pair first if the store has no private key. An existing private key is never
    /// replaced: recordings sealed to it would become unreadable. The store must be open.
    /// </summary>
    public static async Task<byte[]> EnsureAsync(ICredentialStore store)
    {
        if (await PrivateKeyAsync(store) is { } existing)
        {
            try
            {
                return PublicKeyOf(existing);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existing);
            }
        }

        using var key = Key.Create(Agreement, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var raw = key.Export(KeyBlobFormat.RawPrivateKey);
        try
        {
            await store.SaveAsync(new Credential(CredentialId, CredentialName, CredentialKind.Password, null, null, Convert.ToBase64String(raw),
                Notes: "Opens Orrery's session recordings. Deleting it makes every recording made so far unreadable."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }

        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public static byte[] PublicKeyOf(byte[] privateKey)
    {
        using var key = Key.Import(Agreement, privateKey, KeyBlobFormat.RawPrivateKey);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    /// <summary>A new random file key.</summary>
    public static byte[] NewFileKey() => RandomNumberGenerator.GetBytes(FileKeySize);

    /// <summary>Seals <paramref name="fileKey"/> to <paramref name="publicKey"/>.</summary>
    public static SealedFileKey Seal(byte[] publicKey, byte[] fileKey)
    {
        var recipient = PublicKey.Import(Agreement, publicKey, KeyBlobFormat.RawPublicKey);
        using var ephemeral = Key.Create(Agreement);
        var ephemeralPublic = ephemeral.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        using var wrapping = WrappingKey(ephemeral, recipient, ephemeralPublic, publicKey)
            ?? throw new InvalidDataException("The recordings public key isn't a usable key.");
        var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
        return new SealedFileKey(ephemeralPublic, nonce, Aead.Encrypt(wrapping, nonce, ephemeralPublic, fileKey));
    }

    /// <summary>The file key, or null when <paramref name="privateKey"/> isn't the one it was sealed to.</summary>
    public static byte[]? Open(byte[] privateKey, SealedFileKey sealedKey)
    {
        if (sealedKey.EphemeralPublicKey.Length != KeySize || sealedKey.Nonce.Length != Aead.NonceSize
            || sealedKey.Ciphertext.Length != FileKeySize + Aead.TagSize)
        {
            return null;
        }

        using var key = Key.Import(Agreement, privateKey, KeyBlobFormat.RawPrivateKey);
        PublicKey ephemeral;
        try
        {
            ephemeral = PublicKey.Import(Agreement, sealedKey.EphemeralPublicKey, KeyBlobFormat.RawPublicKey);
        }
        catch (FormatException)
        {
            return null;
        }

        var ownPublic = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        using var wrapping = WrappingKey(key, ephemeral, sealedKey.EphemeralPublicKey, ownPublic);
        if (wrapping is null)
        {
            return null;
        }

        return Aead.Decrypt(wrapping, sealedKey.Nonce, sealedKey.EphemeralPublicKey, sealedKey.Ciphertext);
    }

    // Both public keys go into the salt, as in a sealed box, so the result belongs to this pair alone.
    static Key? WrappingKey(Key own, PublicKey other, byte[] ephemeralPublic, byte[] recipientPublic)
    {
        using var shared = Agreement.Agree(own, other);
        if (shared is null)
        {
            return null;
        }

        var salt = new byte[ephemeralPublic.Length + recipientPublic.Length];
        ephemeralPublic.CopyTo(salt, 0);
        recipientPublic.CopyTo(salt, ephemeralPublic.Length);
        return KeyDerivationAlgorithm.HkdfSha256.DeriveKey(shared, salt, Info, Aead);
    }
}

/// <summary>A file key sealed to the recordings public key, as kept in a recording's header.</summary>
public sealed record SealedFileKey(byte[] EphemeralPublicKey, byte[] Nonce, byte[] Ciphertext);

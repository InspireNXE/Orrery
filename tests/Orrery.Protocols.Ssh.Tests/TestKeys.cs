using System.Security.Cryptography;
using System.Text;

namespace Orrery.Protocols.Ssh.Tests;

/// <summary>
/// Throwaway keys made in the test, for files the tests write themselves. Nothing here reads a real key, and
/// none of these ever authenticates anywhere.
/// </summary>
static class TestKeys
{
    static readonly Lazy<string> Rsa = new(() => RSA.Create(2048).ExportRSAPrivateKeyPem());
    static readonly Lazy<string> OtherRsa = new(() => RSA.Create(2048).ExportRSAPrivateKeyPem());

    /// <summary>An RSA key in the traditional PEM format (BEGIN RSA PRIVATE KEY), not protected. The same one each time.</summary>
    public static string RsaPem() => Rsa.Value;

    /// <summary>A second RSA key, different from <see cref="RsaPem"/>.</summary>
    public static string OtherRsaPem() => OtherRsa.Value;

    /// <summary>An ECDSA (nistp256) key in PEM, not protected. A new one each time.</summary>
    public static string EcdsaPem() => ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportECPrivateKeyPem();

    /// <summary>
    /// A new RSA key in the traditional PEM format protected by <paramref name="passphrase"/> (AES-128-CBC, the key
    /// made from the passphrase and the IV with MD5 as OpenSSL does), written from RFC 1421 rather than by a
    /// library SSH.NET shares code with.
    /// </summary>
    public static string EncryptedRsaPem(string passphrase)
    {
        using var rsa = RSA.Create(2048);
        var der = rsa.ExportRSAPrivateKey();
        var iv = RandomNumberGenerator.GetBytes(16);
        var key = MD5.HashData([.. Encoding.UTF8.GetBytes(passphrase), .. iv[..8]]);
        using var aes = Aes.Create();
        aes.Key = key;
        var cipher = aes.EncryptCbc(der, iv, PaddingMode.PKCS7);
        var body = Convert.ToBase64String(cipher, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n");
        return $"-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,{Convert.ToHexString(iv)}\n\n{body}\n-----END RSA PRIVATE KEY-----\n";
    }
}

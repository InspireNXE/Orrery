using Orrery.Core.Import;
using Xunit;

namespace Orrery.Core.Tests.Import;

public class MRemoteNgCryptoTests
{
    // The Protected attribute from a real confCons.xml saved by mRemoteNG (ConfVersion 2.6, AES-GCM,
    // 1000 iterations, no master password). It holds a fixed marker, not a secret.
    const string RealProtectedMarker = "yWtl81J6Tdwrk2wKI2a2V7XgH0K5L7Gh8ue25Fw6KpTARhNK7QebgD+ct62JHKVWYE6clLBOEhxqvm9u704uarwi";

    [Fact]
    public void Decrypts_the_protected_marker_from_a_real_mRemoteNG_file()
    {
        Assert.Equal(MRemoteNgCrypto.NotProtectedMarker,
            MRemoteNgCrypto.TryDecrypt(RealProtectedMarker, MRemoteNgCrypto.DefaultPassword, 1000));
    }

    [Fact]
    public void Wrong_password_or_iterations_return_null()
    {
        Assert.Null(MRemoteNgCrypto.TryDecrypt(RealProtectedMarker, "not it", 1000));
        Assert.Null(MRemoteNgCrypto.TryDecrypt(RealProtectedMarker, MRemoteNgCrypto.DefaultPassword, 999));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sup3r-Secret!")]
    [InlineData("pässwörd with ünïcode")]
    public void Round_trips(string plaintext)
    {
        var encrypted = MRemoteNgCrypto.Encrypt(plaintext, "master", 1000);
        Assert.Equal(plaintext, MRemoteNgCrypto.TryDecrypt(encrypted, "master", 1000));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")]
    public void Garbage_returns_null(string value)
    {
        Assert.Null(MRemoteNgCrypto.TryDecrypt(value, MRemoteNgCrypto.DefaultPassword, 1000));
    }
}

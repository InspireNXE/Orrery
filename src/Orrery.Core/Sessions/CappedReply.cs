namespace Orrery.Core.Sessions;

/// <summary>
/// Puts a size cap on a reply whose length is decided by someone else's parser. A CredSSP reply is usually a
/// TsRequest (a DER SEQUENCE), but the last one under HYBRID_EX is a 4-byte Early User Authorization Result, so
/// the protocol library's own hint decides how much a reply is. This only refuses replies that grow too large.
/// </summary>
public static class CappedReply
{
    /// <summary>
    /// The reply's size once <paramref name="findSize"/> knows it, or null while more bytes are needed. A DER
    /// SEQUENCE is checked against <paramref name="maxSize"/> as soon as its header is in, before it's buffered.
    /// </summary>
    public static int? FindSize(byte[] buffered, Func<byte[], long?> findSize, int maxSize)
    {
        if (buffered.Length >= 2 && buffered[0] == 0x30)
        {
            var headerLength = 2 + DerSequence.ExtraLengthBytes(buffered[1]);
            if (buffered.Length >= headerLength)
            {
                DerSequence.TotalSize(buffered.AsSpan(0, headerLength), maxSize);
            }
        }

        if (findSize(buffered) is { } size)
        {
            return size <= maxSize ? (int)size : throw TooLarge();
        }

        return buffered.Length <= maxSize ? null : throw TooLarge();
    }

    static InvalidDataException TooLarge() => new("The server's sign-in reply was too large.");
}

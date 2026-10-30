namespace Orrery.Core.Sessions;

/// <summary>
/// Reads the header of a DER SEQUENCE, which is what a CredSSP TsRequest is. A server picks the length, so
/// the caller learns the full size from the first few bytes and checks it against a cap before reading the rest.
/// </summary>
public static class DerSequence
{
    const byte SequenceTag = 0x30;

    /// <summary>
    /// How many length bytes come after the first two, given the second byte: none for a short form,
    /// otherwise 1 to 4 for 0x81 to 0x84. Longer forms and the indefinite form are refused.
    /// </summary>
    public static int ExtraLengthBytes(byte second)
    {
        if (second < 0x80)
        {
            return 0;
        }

        var count = second - 0x80;
        return count is >= 1 and <= 4 ? count : throw Invalid();
    }

    /// <summary>
    /// The size of the whole SEQUENCE, header included, from a header made of the tag, the length byte
    /// and the <see cref="ExtraLengthBytes"/> after it. Refuses anything larger than <paramref name="maxSize"/>.
    /// </summary>
    public static int TotalSize(ReadOnlySpan<byte> header, int maxSize)
    {
        if (header.Length < 2 || header[0] != SequenceTag)
        {
            throw Invalid();
        }

        var extra = ExtraLengthBytes(header[1]);
        if (header.Length != 2 + extra)
        {
            throw Invalid();
        }

        long content = extra == 0 ? header[1] : 0;
        for (var i = 0; i < extra; i++)
        {
            content = (content << 8) | header[2 + i];
        }

        var total = header.Length + content;
        return total <= maxSize ? (int)total : throw new InvalidDataException("The server's sign-in reply was too large.");
    }

    static InvalidDataException Invalid() => new("The server's sign-in reply wasn't valid.");
}

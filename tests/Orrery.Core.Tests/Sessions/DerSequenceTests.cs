using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Sessions;

public class DerSequenceTests
{
    const int Max = 64 * 1024;

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x7F, 0)]
    [InlineData(0x81, 1)]
    [InlineData(0x82, 2)]
    [InlineData(0x83, 3)]
    [InlineData(0x84, 4)]
    public void The_second_byte_says_how_many_length_bytes_follow(byte second, int extra)
    {
        Assert.Equal(extra, DerSequence.ExtraLengthBytes(second));
    }

    [Theory]
    [InlineData(0x80)] // indefinite length, not DER
    [InlineData(0x85)] // a length that can't fit in 32 bits
    [InlineData(0xFF)]
    public void Indefinite_and_oversized_length_forms_are_refused(byte second)
    {
        Assert.Throws<InvalidDataException>(() => DerSequence.ExtraLengthBytes(second));
    }

    [Fact]
    public void Short_form_total_is_the_header_plus_the_content()
    {
        // 0x30 0x05: a SEQUENCE with 5 content bytes
        Assert.Equal(7, DerSequence.TotalSize([0x30, 0x05], Max));
    }

    [Fact]
    public void A_one_length_byte_form_counts_three_header_bytes()
    {
        Assert.Equal(3 + 200, DerSequence.TotalSize([0x30, 0x81, 200], Max));
    }

    [Fact]
    public void A_two_length_byte_form_counts_four_header_bytes()
    {
        Assert.Equal(4 + 0x0123, DerSequence.TotalSize([0x30, 0x82, 0x01, 0x23], Max));
    }

    [Fact]
    public void A_reply_of_exactly_the_cap_is_allowed_and_one_more_byte_is_refused()
    {
        Assert.Equal(Max, DerSequence.TotalSize([0x30, 0x83, 0x00, 0xFF, 0xFB], Max));
        var e = Assert.Throws<InvalidDataException>(() => DerSequence.TotalSize([0x30, 0x83, 0x00, 0xFF, 0xFC], Max));
        Assert.Equal("The server's sign-in reply was too large.", e.Message);
    }

    [Fact]
    public void A_four_byte_length_of_four_gigabytes_is_refused_without_overflowing()
    {
        var e = Assert.Throws<InvalidDataException>(() => DerSequence.TotalSize([0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF], Max));
        Assert.Equal("The server's sign-in reply was too large.", e.Message);
        Assert.Throws<InvalidDataException>(() => DerSequence.TotalSize([0x30, 0x84, 0x80, 0x00, 0x00, 0x00], Max));
    }

    [Fact]
    public void Something_that_isnt_a_sequence_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => DerSequence.TotalSize([0x04, 0x05], Max));
        Assert.Equal("The server's sign-in reply wasn't valid.", e.Message);
    }

    [Fact]
    public void A_header_shorter_than_its_length_form_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => DerSequence.TotalSize([0x30, 0x82, 0x01], Max));
    }
}

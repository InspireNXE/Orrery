using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Sessions;

public class CappedReplyTests
{
    const int Max = 64 * 1024;

    [Fact]
    public void An_early_user_authorization_result_is_read_by_its_own_size()
    {
        // HYBRID_EX ends CredSSP with a 4-byte result that isn't DER; it must not be refused as a bad TsRequest.
        byte[] success = [0x00, 0x00, 0x00, 0x00];
        Assert.Equal(4, CappedReply.FindSize(success, _ => 4, Max));
    }

    [Fact]
    public void A_ts_request_takes_the_size_the_hint_finds()
    {
        byte[] header = [0x30, 0x82, 0x01, 0x00];
        Assert.Null(CappedReply.FindSize(header, _ => null, Max));
        Assert.Equal(260, CappedReply.FindSize(header, _ => 260, Max));
    }

    [Fact]
    public void A_ts_request_claiming_more_than_the_cap_is_refused_from_its_header()
    {
        byte[] header = [0x30, 0x84, 0x7F, 0xFF, 0xFF, 0xFF];
        var error = Assert.Throws<InvalidDataException>(() => CappedReply.FindSize(header, _ => null, Max));
        Assert.Equal("The server's sign-in reply was too large.", error.Message);
    }

    [Fact]
    public void A_reply_the_hint_sizes_past_the_cap_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CappedReply.FindSize([0x00], _ => Max + 1L, Max));
    }

    [Fact]
    public void Buffering_past_the_cap_without_a_size_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CappedReply.FindSize(new byte[Max + 1], _ => null, Max));
    }
}

using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

public sealed class RdpRemoteStoppedReadingTests
{
    // As IronRDP's Windows clipboard words it (WinCliprdrError::RemoteStoppedReading in the fork).
    const string IronRdpMessage = "the remote stopped reading the files copied from here: something was copied there while it was pasting them";

    [Fact]
    public void Recognizes_the_error_IronRdp_reports_when_the_server_abandons_a_paste()
    {
        Assert.True(RdpSession.IsRemoteStoppedReading(IronRdpMessage));
    }

    [Theory]
    [InlineData("failed to receive data from remote clipboard")]
    [InlineData("clipboard is busy or denied")]
    [InlineData("")]
    public void Leaves_other_clipboard_errors_alone(string message)
    {
        Assert.False(RdpSession.IsRemoteStoppedReading(message));
    }
}

using System.Net.Sockets;
using Renci.SshNet;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

/// <summary>
/// SSH.NET doesn't hand out its socket, so the round trip reads it from its private members. This fails when an
/// SSH.NET update renames them, rather than the round trip quietly going missing from the Connection card.
/// </summary>
public class SshSocketTests
{
    [Fact]
    public void The_members_the_socket_is_read_from_are_still_there()
    {
        Assert.NotNull(SshSocket.SessionProperty);
        Assert.NotNull(SshSocket.SocketField);
        Assert.Equal(typeof(Socket), SshSocket.SocketField!.FieldType);
    }

    [Fact]
    public void A_client_that_isnt_connected_has_no_socket()
    {
        using var client = new SshClient("192.0.2.1", "nobody", "nothing");
        Assert.Null(SshSocket.Of(client));
    }
}

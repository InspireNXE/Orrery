using System.Net.Sockets;
using Devolutions.IronRdp;
using Orrery.Core.Sessions;
using Orrery.Protocols.Rdp;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>
/// IronRDP's own exceptions, as the connection-lost screen tells them. Here because this is the test project that
/// already has IronRDP; an <see cref="IronRdpException"/> needs a native error, so only the managed ones are built.
/// </summary>
public class RdpFailureTests
{
    static readonly ConnectionTarget Desktop = new("desk", 3389, "me", null);

    static FailureKind KindOf(Exception e) => ConnectionFailure.Classify(RdpSession.Recognise(e), e.Message, Desktop).Kind;

    [Fact]
    public void Its_own_lookup_failing_and_the_server_hanging_up_are_named()
    {
        Assert.Equal(FailureKind.NameNotFound, KindOf(new IronRdpLibException(IronRdpLibExceptionType.CannotResolveDns, "Cannot resolve desk")));
        Assert.Equal(FailureKind.ServerClosed, KindOf(new IronRdpLibException(IronRdpLibExceptionType.EndOfFile, "EOF")));
        Assert.Equal(FailureKind.Other, KindOf(new IronRdpLibException(IronRdpLibExceptionType.ConnectionFailed, "Connection failed")));
    }

    [Fact]
    public void An_untrusted_certificate_needs_the_user()
    {
        var failure = ConnectionFailure.Classify(new CertificateRejectedException("desk"), null, Desktop);
        Assert.Equal(FailureKind.CertificateNotTrusted, failure.Kind);
        Assert.False(failure.CanRetryByItself(dropped: true));
    }

    [Fact]
    public void Socket_errors_and_timeouts_from_the_connection_pass_through()
    {
        Assert.Equal(FailureKind.Refused, KindOf(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Equal(FailureKind.TimedOut, KindOf(new TimeoutException("The server took too long to sign in.")));
        Assert.Equal(FailureKind.ServerClosed, KindOf(new IOException("Unable to read data", new SocketException((int)SocketError.ConnectionReset))));
        Assert.Equal(FailureKind.Other, KindOf(new NotSupportedException("The server doesn't support Network Level Authentication, which Orrery requires.")));
    }
}

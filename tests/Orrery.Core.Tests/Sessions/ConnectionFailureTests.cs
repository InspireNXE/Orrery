using System.Net.Sockets;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Sessions;

public class ConnectionFailureTests
{
    static readonly ConnectionTarget Box = new("box.example", 3389, "admin", null);

    static ConnectionFailure Classify(Exception? error, string? message = null, bool networkAvailable = true) =>
        ConnectionFailure.Classify(error, message, Box, networkAvailable);

    [Theory]
    [InlineData(SocketError.HostNotFound, FailureKind.NameNotFound)]
    [InlineData(SocketError.TryAgain, FailureKind.NameNotFound)]
    [InlineData(SocketError.NoData, FailureKind.NameNotFound)]
    [InlineData(SocketError.ConnectionRefused, FailureKind.Refused)]
    [InlineData(SocketError.TimedOut, FailureKind.TimedOut)]
    [InlineData(SocketError.NetworkUnreachable, FailureKind.Unreachable)]
    [InlineData(SocketError.HostUnreachable, FailureKind.Unreachable)]
    [InlineData(SocketError.NetworkDown, FailureKind.Unreachable)]
    [InlineData(SocketError.ConnectionReset, FailureKind.ServerClosed)]
    [InlineData(SocketError.ConnectionAborted, FailureKind.ServerClosed)]
    [InlineData(SocketError.AccessDenied, FailureKind.Other)]
    public void Socket_errors_say_what_happened(SocketError error, FailureKind kind)
    {
        Assert.Equal(kind, Classify(new SocketException((int)error)).Kind);
    }

    [Fact]
    public void A_socket_error_inside_an_io_error_is_found()
    {
        // What a NetworkStream throws when the connection is reset mid-read.
        var error = new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionReset));
        Assert.Equal(FailureKind.ServerClosed, Classify(error).Kind);
    }

    [Fact]
    public void Timeouts_and_the_stream_ending_are_recognised()
    {
        Assert.Equal(FailureKind.TimedOut, Classify(new TimeoutException("The server took too long to sign in.")).Kind);
        Assert.Equal(FailureKind.ServerClosed, Classify(new EndOfStreamException()).Kind);
        Assert.Equal(FailureKind.TimedOut, Classify(new AggregateException(new TimeoutException())).Kind);
    }

    [Fact]
    public void What_a_protocol_recognised_wins_over_what_is_inside_it()
    {
        var error = new SessionFailureException(FailureKind.LoginFailed, "Refused", new SocketException((int)SocketError.ConnectionReset));
        Assert.Equal(FailureKind.LoginFailed, Classify(error).Kind);
    }

    [Fact]
    public void Nothing_known_keeps_the_raw_message()
    {
        var failure = Classify(new NotSupportedException("The server redirected the connection."), "The server redirected the connection.");
        Assert.Equal(FailureKind.Other, failure.Kind);
        Assert.Equal("The server redirected the connection.", failure.Summary);
        Assert.Null(failure.Advice);
        Assert.Contains("NotSupportedException: The server redirected the connection.", failure.Detail);

        var bare = Classify(null, "Something odd");
        Assert.Equal("Something odd", bare.Summary);
        Assert.Equal("Something odd", bare.Detail);
    }

    [Fact]
    public void Each_kind_names_the_host_in_one_plain_sentence()
    {
        Assert.Equal("Couldn't find a computer called box.example.", Classify(new SocketException((int)SocketError.HostNotFound)).Summary);
        Assert.Equal("box.example turned down the connection on port 3389.", Classify(new SocketException((int)SocketError.ConnectionRefused)).Summary);
        Assert.Equal("box.example didn't answer in time.", Classify(new TimeoutException()).Summary);
        Assert.Equal("box.example closed the connection.", Classify(new EndOfStreamException()).Summary);
        foreach (var kind in Enum.GetValues<FailureKind>().Where(k => k is not FailureKind.Other and not FailureKind.JumpHost))
        {
            var failure = Classify(new SessionFailureException(kind, "x"));
            Assert.EndsWith(".", failure.Summary);
            Assert.False(string.IsNullOrWhiteSpace(failure.Advice));
        }
    }

    [Fact]
    public void Without_a_network_a_failed_lookup_is_put_down_to_being_offline()
    {
        var failure = Classify(new SocketException((int)SocketError.HostNotFound), networkAvailable: false);
        Assert.Equal(FailureKind.Offline, failure.Kind);
        Assert.Equal("This computer isn't connected to a network.", failure.Summary);

        // A refusal came from somewhere, so it isn't put down to being offline.
        Assert.Equal(FailureKind.Refused, Classify(new SocketException((int)SocketError.ConnectionRefused), networkAvailable: false).Kind);
    }

    [Fact]
    public void A_jump_host_failure_names_the_hop_and_what_went_wrong_there()
    {
        var hop = new ConnectionTarget("bastion", 22, "me", null);
        var error = new RouteException("Jump host bastion: refused", new SocketException((int)SocketError.ConnectionRefused), hop);

        var failure = Classify(error, error.Message);

        Assert.Equal(FailureKind.JumpHost, failure.Kind);
        Assert.Equal(FailureKind.Refused, failure.Cause);
        Assert.Equal("bastion", failure.Hop);
        Assert.Equal("Couldn't get through the jump host bastion.", failure.Summary);
        Assert.StartsWith("bastion turned down the connection on port 22.", failure.Advice);
        Assert.True(failure.IsNetwork);
    }

    [Fact]
    public void A_route_without_a_hop_is_classified_by_what_it_holds()
    {
        Assert.Equal(FailureKind.TimedOut, Classify(new RouteException("tunnel", new TimeoutException())).Kind);
    }

    [Theory]
    [InlineData(FailureKind.LoginFailed)]
    [InlineData(FailureKind.HostKeyChanged)]
    [InlineData(FailureKind.HostKeyNotTrusted)]
    [InlineData(FailureKind.CertificateNotTrusted)]
    public void Logins_host_keys_and_certificates_are_never_retried_by_themselves(FailureKind kind)
    {
        var failure = Classify(new SessionFailureException(kind, "x"));
        Assert.True(failure.NeedsUser);
        Assert.False(failure.CanRetryByItself(dropped: true));
        Assert.False(failure.CanRetryByItself(dropped: false));

        // Nor at a jump host, where repeated logins could lock the account just the same.
        var atHop = Classify(new RouteException("hop", new SessionFailureException(kind, "x"), new ConnectionTarget("bastion", 22, null, null)));
        Assert.False(atHop.CanRetryByItself(dropped: true));
    }

    [Theory]
    [InlineData(FailureKind.NameNotFound, false, true)]
    [InlineData(FailureKind.Refused, false, true)]
    [InlineData(FailureKind.TimedOut, false, true)]
    [InlineData(FailureKind.Unreachable, false, true)]
    [InlineData(FailureKind.Offline, false, true)]
    [InlineData(FailureKind.ServerClosed, false, false)]
    [InlineData(FailureKind.ServerClosed, true, true)]
    [InlineData(FailureKind.Other, false, false)]
    [InlineData(FailureKind.Other, true, true)]
    public void Drops_and_network_errors_are_retried(FailureKind kind, bool dropped, bool retried)
    {
        Assert.Equal(retried, Classify(new SessionFailureException(kind, "x")).CanRetryByItself(dropped));
    }

    [Fact]
    public void Tries_start_a_few_seconds_apart_grow_and_stop_growing_at_a_minute()
    {
        var delays = Enumerable.Range(1, ReconnectSchedule.MaxAttempts).Select(ReconnectSchedule.Delay).ToList();
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
        Assert.All(delays.Zip(delays.Skip(1)), pair => Assert.True(pair.Second >= pair.First));
        Assert.Equal(TimeSpan.FromSeconds(60), delays[^1]);
        Assert.Equal(TimeSpan.FromSeconds(60), ReconnectSchedule.Delay(100));
    }
}

using System.Net.Sockets;
using System.Text;

namespace Orrery.Core.Sessions;

/// <summary>What kind of thing stopped a connection, as the connection-lost screen tells it.</summary>
public enum FailureKind
{
    Other,
    NameNotFound,
    Refused,
    TimedOut,
    Unreachable,
    Offline,
    JumpHost,
    LoginFailed,
    HostKeyChanged,
    HostKeyNotTrusted,
    CertificateNotTrusted,
    ServerClosed,
}

/// <summary>
/// A failure a protocol has recognised itself, where .NET's own exceptions wouldn't say what happened: a refused
/// login, a host key or certificate turned down, the server ending the connection. The library's own exception
/// is kept as the inner one, for the details.
/// </summary>
public class SessionFailureException(FailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public FailureKind Kind { get; } = kind;
}

/// <summary>
/// Why a session failed to connect or dropped, in one plain sentence with what to try, and whether Orrery may try
/// again by itself. Made from the exception a session reports; see <see cref="Classify"/>.
/// </summary>
/// <param name="Summary">What happened, in one sentence.</param>
/// <param name="Advice">What to try, when there's something to say.</param>
/// <param name="Detail">The technical messages, for the Details section and for copying.</param>
public sealed record ConnectionFailure(FailureKind Kind, string Summary, string? Advice, string Detail)
{
    /// <summary>For <see cref="FailureKind.JumpHost"/>: the jump host that failed.</summary>
    public string? Hop { get; init; }

    /// <summary>What went wrong: the same as <see cref="Kind"/>, except at a jump host, where it's what went wrong there.</summary>
    public FailureKind Cause { get; init; }

    /// <summary>
    /// A refused login, or a host key or certificate turned down. Trying again by itself would only fail the same
    /// way, and repeated logins can lock an account, so these wait for the user.
    /// </summary>
    public bool NeedsUser => Cause is FailureKind.LoginFailed or FailureKind.HostKeyChanged or FailureKind.HostKeyNotTrusted or FailureKind.CertificateNotTrusted;

    /// <summary>The network or the far end's address: worth trying again once things change.</summary>
    public bool IsNetwork => Cause is FailureKind.NameNotFound or FailureKind.Refused or FailureKind.TimedOut or FailureKind.Unreachable or FailureKind.Offline;

    /// <summary>
    /// Whether Orrery may reconnect without being asked: after a drop (<paramref name="dropped"/>) or a network
    /// error, and never when it <see cref="NeedsUser"/>.
    /// </summary>
    public bool CanRetryByItself(bool dropped) => !NeedsUser && (dropped || IsNetwork);

    /// <summary>
    /// Works out what went wrong from <paramref name="error"/> and its inner exceptions: .NET's socket errors and
    /// timeouts, <see cref="SessionFailureException"/> from the protocols, and <see cref="RouteException"/> for jump
    /// hosts. <paramref name="message"/> is the session's own message, used when nothing more is known.
    /// </summary>
    /// <param name="networkAvailable">What the OS says; when it has no network, a lookup or route failure is put down to that.</param>
    public static ConnectionFailure Classify(Exception? error, string? message, ConnectionTarget target, bool networkAvailable = true)
    {
        var detail = Describe(error, message);
        if (Chain(error).OfType<RouteException>().FirstOrDefault(r => r.Hop is not null) is { Hop: { } hop } route)
        {
            var cause = Classify(route.InnerException, route.Message, hop, networkAvailable);
            return new ConnectionFailure(FailureKind.JumpHost, $"Couldn't get through the jump host {hop.Host}.", Join(cause.Summary, cause.Advice), detail)
            {
                Hop = hop.Host,
                Cause = cause.Kind,
            };
        }

        var kind = KindOf(error);
        if (!networkAvailable && kind is FailureKind.NameNotFound or FailureKind.Unreachable or FailureKind.TimedOut)
        {
            kind = FailureKind.Offline;
        }

        var raw = string.IsNullOrWhiteSpace(message) ? error?.Message : message;
        var (summary, advice) = Words(kind, target, raw);
        return new ConnectionFailure(kind, summary, advice, detail) { Cause = kind };
    }

    static FailureKind KindOf(Exception? error)
    {
        foreach (var e in Chain(error))
        {
            switch (e)
            {
                case SessionFailureException failure:
                    return failure.Kind;
                case SocketException socket when OfSocket(socket.SocketErrorCode) is { } kind:
                    return kind;
                case TimeoutException:
                    return FailureKind.TimedOut;
                case EndOfStreamException:
                    return FailureKind.ServerClosed;
            }
        }

        return FailureKind.Other;
    }

    static FailureKind? OfSocket(SocketError error) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => FailureKind.NameNotFound,
        SocketError.ConnectionRefused => FailureKind.Refused,
        SocketError.TimedOut => FailureKind.TimedOut,
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.HostDown => FailureKind.Unreachable,
        SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown or SocketError.Disconnecting or SocketError.NotConnected => FailureKind.ServerClosed,
        _ => null,
    };

    static (string Summary, string? Advice) Words(FailureKind kind, ConnectionTarget target, string? raw)
    {
        string host = target.Host;
        return kind switch
        {
            FailureKind.NameNotFound => ($"Couldn't find a computer called {host}.", "Check the name is spelt right, or try its IP address."),
            FailureKind.Refused => ($"{host} turned down the connection on port {target.Port}.", "Check the port, and that the remote service is running."),
            FailureKind.TimedOut => ($"{host} didn't answer in time.", $"Check it's switched on, and that a firewall isn't blocking port {target.Port}."),
            FailureKind.Unreachable => ($"There's no network route to {host}.", "Check your network connection, or your VPN if it's on a private network."),
            FailureKind.Offline => ("This computer isn't connected to a network.", "Check the Wi-Fi or network cable."),
            FailureKind.LoginFailed => ($"{host} didn't accept the login.", "Check the username and password, or the key, then reconnect."),
            FailureKind.HostKeyChanged => ($"The host key of {host} has changed, and it wasn't trusted.",
                "A reinstalled server gets a new key, but it can also mean someone is intercepting the connection. Check with whoever runs the server before you trust it."),
            FailureKind.HostKeyNotTrusted => ($"The host key of {host} wasn't trusted, so Orrery didn't connect.", "Reconnect to check the key again."),
            FailureKind.CertificateNotTrusted => ($"The certificate of {host} wasn't trusted, so Orrery didn't connect.", "Reconnect to check the certificate again."),
            FailureKind.ServerClosed => ($"{host} closed the connection.", "It may have restarted, or ended the session."),
            _ => (string.IsNullOrWhiteSpace(raw) ? "Something went wrong with the connection." : raw.Trim(), null),
        };
    }

    /// <summary>The session's message, then each exception in the chain with its type, for the Details section.</summary>
    static string Describe(Exception? error, string? message)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message))
        {
            text.AppendLine(message.Trim());
        }

        foreach (var e in Chain(error))
        {
            text.Append(e.GetType().Name);
            if (e is SocketException socket)
            {
                text.Append($" ({socket.SocketErrorCode})");
            }

            text.Append(": ").AppendLine(e.Message);
        }

        return text.Length == 0 ? "No more is known." : text.ToString().TrimEnd();
    }

    /// <summary><paramref name="error"/> and its inner exceptions, outermost first.</summary>
    static IEnumerable<Exception> Chain(Exception? error)
    {
        for (int depth = 0; error is not null && depth < 16; depth++)
        {
            yield return error;
            error = error is AggregateException { InnerExceptions.Count: > 0 } aggregate ? aggregate.InnerExceptions[0] : error.InnerException;
        }
    }

    static string Join(string first, string? second) => second is null ? first : $"{first} {second}";
}

/// <summary>When Orrery tries again by itself: a few seconds at first, doubling, at most a minute apart.</summary>
public static class ReconnectSchedule
{
    public const int MaxAttempts = 10;
    static readonly TimeSpan Longest = TimeSpan.FromSeconds(60);

    /// <param name="attempt">1 for the first try.</param>
    public static TimeSpan Delay(int attempt) =>
        attempt >= 6 ? Longest : TimeSpan.FromSeconds(Math.Min(Longest.TotalSeconds, 2 << Math.Max(0, attempt - 1)));
}

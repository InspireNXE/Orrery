namespace Orrery.Core.Sessions;

/// <summary>
/// A way to reach a remote machine other than dialling it directly, such as an SSH tunnel through jump hosts.
/// A session opens it before connecting, dials the address it returns instead of the machine's own, and
/// disposes it when the session ends. Names (certificates, host keys, Kerberos) still use the machine's own.
/// </summary>
public interface ISessionRoute : IDisposable
{
    /// <summary>
    /// Opens the way to <paramref name="host"/>:<paramref name="port"/> and returns where to dial instead.
    /// Throws <see cref="RouteException"/>, with a message for the user, when it can't.
    /// </summary>
    Task<(string Host, int Port)> OpenAsync(string host, int port, CancellationToken token);
}

/// <summary>The route couldn't be opened; the message says which hop and why.</summary>
public sealed class RouteException(string message, Exception? inner = null) : Exception(message, inner);

public static class SessionRoute
{
    /// <summary>Opens <paramref name="route"/> to the target, or returns the target itself when there's none.</summary>
    public static Task<(string Host, int Port)> DialAsync(this ISessionRoute? route, ConnectionTarget target, CancellationToken token) =>
        route is null ? Task.FromResult((target.Host, target.Port)) : route.OpenAsync(target.Host, target.Port, token);
}

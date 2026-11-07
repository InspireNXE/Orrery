using System.Net.Sockets;
using System.Reflection;
using Renci.SshNet;

namespace Orrery.Protocols.Ssh;

/// <summary>
/// The socket under an SSH.NET client, for the round trip the session pane shows. SSH.NET doesn't hand it out, so it's
/// read from its private members (BaseClient.Session, then Session._socket); a version that renames them gives null,
/// and the pane leaves the round trip out. A test checks the names against the version Orrery ships with.
/// </summary>
static class SshSocket
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static readonly PropertyInfo? SessionProperty = typeof(BaseClient).GetProperty("Session", Hidden);
    internal static readonly FieldInfo? SocketField = typeof(BaseClient).Assembly.GetType("Renci.SshNet.Session")?.GetField("_socket", Hidden);

    public static Socket? Of(BaseClient client)
    {
        try
        {
            var session = SessionProperty?.GetValue(client);
            return session is null || SocketField is null || !SocketField.DeclaringType!.IsInstanceOfType(session)
                ? null
                : SocketField.GetValue(session) as Socket;
        }
        catch (Exception e) when (e is TargetInvocationException or MemberAccessException or ArgumentException)
        {
            return null;
        }
    }
}

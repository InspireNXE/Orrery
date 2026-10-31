using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Orrery.Protocols.Ssh.Agent;

/// <summary>A key the agent holds: the public key in SSH wire format, its type ("ssh-ed25519") and comment.</summary>
public sealed record AgentIdentity(byte[] PublicKey, string KeyType, string Comment);

/// <summary>
/// Carries one agent request and returns the reply, both including the 4-byte length prefix. Throws
/// <see cref="TimeoutException"/> when the agent takes longer than <c>timeout</c>.
/// </summary>
public interface IAgentTransport
{
    byte[] Exchange(byte[] request, TimeSpan timeout);
}

/// <summary>
/// Talks to an SSH agent (draft-miller-ssh-agent): lists its keys and asks it to sign, so private keys stay
/// inside the agent. Finds the agent the way OpenSSH does: <c>SSH_AUTH_SOCK</c>, then on Windows the
/// OpenSSH agent's pipe, then Pageant's pipe, then an older Pageant's window. On Windows an agent is only used when this user or SYSTEM runs it
/// (see <see cref="AgentOwner"/>).
/// </summary>
public sealed class SshAgentClient(IAgentTransport transport, string description)
{
    const byte Failure = 5, RequestIdentities = 11, IdentitiesAnswer = 12, SignRequest = 13, SignResponse = 14;
    const string WindowsOpenSshPipe = "openssh-ssh-agent";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Where the agent was found, for messages ("Windows OpenSSH agent", "Pageant").</summary>
    public string Description { get; } = description;

    /// <summary>How long the agent may take to list its keys.</summary>
    internal TimeSpan ListTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the agent may take to sign; some ask the user first.</summary>
    internal TimeSpan SignTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The running agent, or null when there isn't one.</summary>
    public static SshAgentClient? Find()
    {
        if (Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } socket)
        {
            return new SshAgentClient(new StreamTransport(() => OpenSocket(socket)), $"SSH_AUTH_SOCK ({socket})");
        }

        if (OperatingSystem.IsWindows())
        {
            if (IsTrustedPipe(WindowsOpenSshPipe))
            {
                return new SshAgentClient(new StreamTransport(OpenWindowsOpenSshPipe), "Windows OpenSSH agent");
            }

            // Pageant's own pipe first; the window and shared memory only for versions before 0.75.
            var pageantPipe = PageantPipe.Name();
            if (IsTrustedPipe(pageantPipe, allowSystem: false))
            {
                return new SshAgentClient(new StreamTransport(() => OpenPageantPipe(pageantPipe)), "Pageant");
            }

            if (PageantTransport.IsRunning())
            {
                return new SshAgentClient(new PageantTransport(), "Pageant (before 0.75)");
            }
        }

        return null;
    }

    public IReadOnlyList<AgentIdentity> ListIdentities()
    {
        var reply = Request([RequestIdentities], ListTimeout);
        if (reply[0] != IdentitiesAnswer)
        {
            throw new IOException("The SSH agent didn't list its keys.");
        }

        var reader = new WireReader(reply.AsSpan(1).ToArray());
        uint count = reader.UInt32();
        var identities = new List<AgentIdentity>((int)Math.Min(count, 1024));
        for (uint i = 0; i < count; i++)
        {
            var blob = reader.String();
            var comment = Encoding.UTF8.GetString(reader.String());
            identities.Add(new AgentIdentity(blob, Encoding.ASCII.GetString(new WireReader(blob).String()), comment));
        }

        return identities;
    }

    /// <summary>
    /// The agent's signature over <paramref name="data"/> (SSH wire format: algorithm name, then signature),
    /// or null if it refused, e.g. the user declined a confirmation.
    /// </summary>
    public byte[]? Sign(byte[] publicKey, byte[] data, uint flags)
    {
        var request = new WireWriter();
        request.Byte(SignRequest);
        request.String(publicKey);
        request.String(data);
        request.UInt32(flags);

        var reply = Request(request.ToArray(), SignTimeout);
        return reply[0] switch
        {
            SignResponse => new WireReader(reply.AsSpan(1).ToArray()).String(),
            Failure => null,
            _ => throw new IOException("The SSH agent sent an unexpected reply."),
        };
    }

    /// <summary>
    /// Whether the pipe exists and its server passes the owner check. That takes one connection, opened at
    /// identification level and closed without sending anything; when it fails, Pageant is tried next.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static bool IsTrustedPipe(string name, bool allowSystem = true)
    {
        if (!AgentOwner.PipeExists(name))
        {
            return false;
        }

        try
        {
            OpenPipe(name, allowSystem).Dispose();
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Not using the SSH agent pipe {name}: {e.Message}");
            return false;
        }
    }

    byte[] Request(byte[] body, TimeSpan timeout)
    {
        var message = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(message, (uint)body.Length);
        body.CopyTo(message, 4);

        var reply = transport.Exchange(message, timeout);
        if (reply.Length < 5 || BinaryPrimitives.ReadUInt32BigEndian(reply) != reply.Length - 4)
        {
            throw new IOException("The SSH agent's reply was cut short.");
        }

        return reply[4..];
    }

    static Stream OpenSocket(string path)
    {
        // Windows OpenSSH also accepts a pipe path here.
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("//./pipe/", StringComparison.OrdinalIgnoreCase)))
        {
            return OpenPipe(path[9..]);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    static Stream OpenWindowsOpenSshPipe() => OpenPipe(WindowsOpenSshPipe);

    [SupportedOSPlatform("windows")]
    static Stream OpenPageantPipe(string name) => OpenPipe(name, allowSystem: false);

    /// <summary>
    /// Connects at identification level, so the server can't act as this user, and checks who the server is
    /// before anything is sent. Asynchronous, so a read that takes too long can be cancelled.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static Stream OpenPipe(string name, bool allowSystem = true)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(ConnectTimeout);
            AgentOwner.CheckPipe(pipe.SafePipeHandle, allowSystem);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>One connection per request: the OpenSSH agent, Pageant's pipe and SSH_AUTH_SOCK sockets.</summary>
    sealed class StreamTransport(Func<Stream> open) : IAgentTransport
    {
        public byte[] Exchange(byte[] request, TimeSpan timeout)
        {
            using var stream = open();
            using var cancel = new CancellationTokenSource(timeout);
            try
            {
                // Pipes don't support ReadTimeout, so the timeout cancels the reads instead. Only ever
                // called from background threads, so waiting here can't deadlock.
                return ExchangeAsync(stream, request, cancel.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw new TimeoutException("The SSH agent didn't answer in time.");
            }
        }

        static async Task<byte[]> ExchangeAsync(Stream stream, byte[] request, CancellationToken cancel)
        {
            await stream.WriteAsync(request, cancel);
            await stream.FlushAsync(cancel);

            var length = new byte[4];
            await stream.ReadExactlyAsync(length, cancel);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(length);
            if (size is <= 0 or > 256 * 1024)
            {
                throw new IOException("The SSH agent's reply has an impossible length.");
            }

            var reply = new byte[4 + size];
            length.CopyTo(reply, 0);
            await stream.ReadExactlyAsync(reply.AsMemory(4), cancel);
            return reply;
        }
    }
}

/// <summary>SSH wire encoding: big-endian uint32, and strings as a length then bytes.</summary>
sealed class WireWriter
{
    readonly MemoryStream _stream = new();

    public void Byte(byte value) => _stream.WriteByte(value);

    public void UInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void String(ReadOnlySpan<byte> value)
    {
        UInt32((uint)value.Length);
        _stream.Write(value);
    }

    public void String(string value) => String(Encoding.UTF8.GetBytes(value));

    public byte[] ToArray() => _stream.ToArray();
}

sealed class WireReader(byte[] data)
{
    int _position;

    public uint UInt32()
    {
        if (data.Length - _position < 4) throw new IOException("The SSH agent's reply was cut short.");
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_position));
        _position += 4;
        return value;
    }

    public byte[] String()
    {
        int length = (int)UInt32();
        // Compared this way round so a length near int.MaxValue can't wrap.
        if (length < 0 || length > data.Length - _position) throw new IOException("The SSH agent's reply was cut short.");
        var value = data[_position..(_position + length)];
        _position += length;
        return value;
    }
}

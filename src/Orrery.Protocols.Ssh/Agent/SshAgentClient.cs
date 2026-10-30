using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace Orrery.Protocols.Ssh.Agent;

/// <summary>A key the agent holds: the public key in SSH wire format, its type ("ssh-ed25519") and comment.</summary>
public sealed record AgentIdentity(byte[] PublicKey, string KeyType, string Comment);

/// <summary>Carries one agent request and returns the reply, both including the 4-byte length prefix.</summary>
public interface IAgentTransport
{
    byte[] Exchange(byte[] request);
}

/// <summary>
/// Talks to an SSH agent (draft-miller-ssh-agent): lists its keys and asks it to sign, so private keys stay
/// inside the agent. Finds the agent the way OpenSSH does: <c>SSH_AUTH_SOCK</c>, then on Windows the
/// OpenSSH agent's pipe, then Pageant.
/// </summary>
public sealed class SshAgentClient(IAgentTransport transport, string description)
{
    const byte Failure = 5, RequestIdentities = 11, IdentitiesAnswer = 12, SignRequest = 13, SignResponse = 14;
    const string WindowsOpenSshPipe = "openssh-ssh-agent";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Where the agent was found, for messages ("Windows OpenSSH agent", "Pageant").</summary>
    public string Description { get; } = description;

    /// <summary>The running agent, or null when there isn't one.</summary>
    public static SshAgentClient? Find()
    {
        if (Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } socket)
        {
            return new SshAgentClient(new StreamTransport(() => OpenSocket(socket)), $"SSH_AUTH_SOCK ({socket})");
        }

        if (OperatingSystem.IsWindows())
        {
            if (File.Exists($@"\\.\pipe\{WindowsOpenSshPipe}"))
            {
                return new SshAgentClient(new StreamTransport(() => OpenPipe(WindowsOpenSshPipe)), "Windows OpenSSH agent");
            }

            if (PageantTransport.IsRunning())
            {
                return new SshAgentClient(new PageantTransport(), "Pageant");
            }
        }

        return null;
    }

    public IReadOnlyList<AgentIdentity> ListIdentities()
    {
        var reply = Request([RequestIdentities]);
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

        var reply = Request(request.ToArray());
        return reply[0] switch
        {
            SignResponse => new WireReader(reply.AsSpan(1).ToArray()).String(),
            Failure => null,
            _ => throw new IOException("The SSH agent sent an unexpected reply."),
        };
    }

    byte[] Request(byte[] body)
    {
        var message = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(message, (uint)body.Length);
        body.CopyTo(message, 4);

        var reply = transport.Exchange(message);
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

    static Stream OpenPipe(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        try
        {
            pipe.Connect(ConnectTimeout);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>One connection per request: the OpenSSH agent and SSH_AUTH_SOCK sockets.</summary>
    sealed class StreamTransport(Func<Stream> open) : IAgentTransport
    {
        public byte[] Exchange(byte[] request)
        {
            using var stream = open();
            stream.Write(request);
            stream.Flush();

            var length = new byte[4];
            stream.ReadExactly(length);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(length);
            if (size is <= 0 or > 256 * 1024)
            {
                throw new IOException("The SSH agent's reply has an impossible length.");
            }

            var reply = new byte[4 + size];
            length.CopyTo(reply, 0);
            stream.ReadExactly(reply.AsSpan(4));
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
        if (_position + 4 > data.Length) throw new IOException("The SSH agent's reply was cut short.");
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_position));
        _position += 4;
        return value;
    }

    public byte[] String()
    {
        int length = (int)UInt32();
        if (length < 0 || _position + length > data.Length) throw new IOException("The SSH agent's reply was cut short.");
        var value = data[_position..(_position + length)];
        _position += length;
        return value;
    }
}

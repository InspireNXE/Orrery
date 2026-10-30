using System.Buffers.Binary;
using System.IO.Pipes;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Orrery.Protocols.Ssh.Tests;

/// <summary>
/// An SSH agent for tests (draft-miller-ssh-agent): holds a dummy RSA key and a dummy ECDSA P-256 key,
/// lists them, and signs. It can serve over a named pipe, or answer messages handed to it directly.
/// </summary>
sealed class FakeAgent : IDisposable
{
    public readonly RSA Rsa = RSA.Create(2048);
    public readonly ECDsa Ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public readonly List<uint> SignFlags = [];
    public bool RefuseSigning { get; set; }

    readonly CancellationTokenSource _cts = new();

    public byte[] RsaBlob => Blob(w => { w.String("ssh-rsa"); w.MpInt(Rsa.ExportParameters(false).Exponent!); w.MpInt(Rsa.ExportParameters(false).Modulus!); });

    public byte[] EcdsaBlob => Blob(w =>
    {
        var q = Ecdsa.ExportParameters(false).Q;
        w.String("ecdsa-sha2-nistp256");
        w.String("nistp256");
        w.String([0x04, .. q.X!, .. q.Y!]);
    });

    /// <summary>Answers one agent message (length prefix included) with the reply (length prefix included).</summary>
    public byte[] Handle(byte[] message)
    {
        var body = message[4..];
        var reply = new Writer();
        switch (body[0])
        {
            case 11: // REQUEST_IDENTITIES
                reply.Byte(12);
                reply.UInt32(2);
                reply.String(RsaBlob); reply.String("dummy rsa key");
                reply.String(EcdsaBlob); reply.String("dummy ecdsa key");
                break;

            case 13: // SIGN_REQUEST
                var reader = new Reader(body[1..]);
                var key = reader.String();
                var data = reader.String();
                uint flags = reader.UInt32();
                lock (SignFlags) SignFlags.Add(flags);
                if (RefuseSigning)
                {
                    reply.Byte(5);
                    break;
                }

                reply.Byte(14);
                reply.String(key.AsSpan().SequenceEqual(RsaBlob) ? SignRsa(data, flags) : SignEcdsa(data));
                break;

            default:
                reply.Byte(5); // FAILURE
                break;
        }

        var bytes = reply.ToArray();
        return [.. BigEndian((uint)bytes.Length), .. bytes];
    }

    byte[] SignRsa(byte[] data, uint flags)
    {
        var (name, hash) = flags switch
        {
            4 => ("rsa-sha2-512", HashAlgorithmName.SHA512),
            2 => ("rsa-sha2-256", HashAlgorithmName.SHA256),
            _ => ("ssh-rsa", HashAlgorithmName.SHA1),
        };
        return Blob(w => { w.String(name); w.String(Rsa.SignData(data, hash, RSASignaturePadding.Pkcs1)); });
    }

    byte[] SignEcdsa(byte[] data)
    {
        var raw = Ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var inner = Blob(w => { w.MpInt(raw[..32]); w.MpInt(raw[32..]); });
        return Blob(w => { w.String("ecdsa-sha2-nistp256"); w.String(inner); });
    }

    /// <summary>Serves on <c>\\.\pipe\name</c>, one request per connection or several, until disposed.</summary>
    public void ServePipe(string name) => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }

            _ = Task.Run(async () =>
            {
                await using var connection = pipe;
                try
                {
                    while (true)
                    {
                        var length = new byte[4];
                        await connection.ReadExactlyAsync(length, _cts.Token);
                        var message = new byte[4 + BinaryPrimitives.ReadUInt32BigEndian(length)];
                        length.CopyTo(message, 0);
                        await connection.ReadExactlyAsync(message.AsMemory(4), _cts.Token);
                        await connection.WriteAsync(Handle(message), _cts.Token);
                        await connection.FlushAsync(_cts.Token);
                    }
                }
                catch (Exception e) when (e is EndOfStreamException or IOException or OperationCanceledException)
                {
                }
            });
        }
    });

    static byte[] Blob(Action<Writer> write) { var w = new Writer(); write(w); return w.ToArray(); }
    static byte[] BigEndian(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }

    public void Dispose()
    {
        _cts.Cancel();
        Rsa.Dispose();
        Ecdsa.Dispose();
    }

    sealed class Writer
    {
        readonly MemoryStream _s = new();
        public void Byte(byte b) => _s.WriteByte(b);
        public void UInt32(uint v) => _s.Write(BigEndian(v));
        public void String(ReadOnlySpan<byte> v) { UInt32((uint)v.Length); _s.Write(v); }
        public void String(string v) => String(Encoding.UTF8.GetBytes(v));

        /// <summary>RFC 4251 mpint from an unsigned big-endian number.</summary>
        public void MpInt(byte[] unsigned)
        {
            var value = new BigInteger(unsigned, isUnsigned: true, isBigEndian: true);
            String(value.IsZero ? [] : value.ToByteArray(isUnsigned: false, isBigEndian: true));
        }

        public byte[] ToArray() => _s.ToArray();
    }

    sealed class Reader(byte[] data)
    {
        int _p;
        public uint UInt32() { var v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_p)); _p += 4; return v; }
        public byte[] String() { int n = (int)UInt32(); var v = data[_p..(_p + n)]; _p += n; return v; }
    }
}

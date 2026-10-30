using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Orrery.Protocols.Vnc.Tests;

/// <summary>
/// A minimal RFB 3.8 server for tests (RFC 6143): no-auth or VNC Authentication, one client, Raw updates of
/// a fixed picture in whatever pixel format the client asks for. It records the input it receives.
/// The desktop it announces can be larger than the picture, which then sits in its top-left corner.
/// </summary>
sealed class TestRfbServer : IAsyncDisposable
{
    public const int Width = 64, Height = 48;

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly string? _password;
    readonly ushort _desktopWidth, _desktopHeight;
    readonly CancellationTokenSource _cts = new();
    readonly Task _serve;

    // Server's own pixel format until the client sets one: 32 bpp little-endian, red at bit 16.
    int _bpp = 32, _redShift = 16, _greenShift = 8, _blueShift = 0, _redMax = 255, _greenMax = 255, _blueMax = 255;
    bool _bigEndian;

    public TestRfbServer(string? password, ushort desktopWidth = Width, ushort desktopHeight = Height)
    {
        _password = password;
        (_desktopWidth, _desktopHeight) = (desktopWidth, desktopHeight);
        _listener.Start();
        _serve = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<(bool Down, uint Keysym)> Keys { get; } = new();
    public ConcurrentQueue<(byte Mask, int X, int Y)> Pointer { get; } = new();
    public ConcurrentQueue<string> Log { get; } = new();
    public ConcurrentQueue<string> ClientClipboard { get; } = new();
    public ConcurrentQueue<int> Encodings { get; } = new();

    readonly SemaphoreSlim _writeLock = new(1, 1);
    Stream? _stream;

    /// <summary>Sends ServerCutText: the server's clipboard changed (Latin-1, as RFC 6143 says).</summary>
    public async Task SendClipboardAsync(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        var message = new byte[] { 3, 0, 0, 0 }.Concat(BigEndian((uint)bytes.Length)).Concat(bytes).ToArray();
        await WriteAsync(message, _cts.Token);
    }

    async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            await _stream!.WriteAsync(bytes, token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The picture: red left half, blue right half, and a green pixel at (5, 7).</summary>
    public static (byte R, byte G, byte B) PixelAt(int x, int y) =>
        (x, y) == (5, 7) ? ((byte)0, (byte)255, (byte)0) : x < Width / 2 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255);

    async Task ServeAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            var stream = client.GetStream();
            var token = _cts.Token;

            await stream.WriteAsync("RFB 003.008\n"u8.ToArray(), token);
            await ReadExactAsync(stream, 12, token);

            byte securityType = _password is null ? (byte)1 : (byte)2;
            await stream.WriteAsync(new byte[] { 1, securityType }, token);
            var chosen = (await ReadExactAsync(stream, 1, token))[0];
            Log.Enqueue($"security {chosen}");

            if (chosen == 2)
            {
                var challenge = RandomNumberGenerator.GetBytes(16);
                await stream.WriteAsync(challenge, token);
                var response = await ReadExactAsync(stream, 16, token);
                if (!response.AsSpan().SequenceEqual(VncAuthResponse(_password!, challenge)))
                {
                    var reason = "Authentication failed"u8.ToArray();
                    await stream.WriteAsync(BigEndian(1u).Concat(BigEndian((uint)reason.Length)).Concat(reason).ToArray(), token);
                    Log.Enqueue("auth rejected");
                    return;
                }
            }

            await stream.WriteAsync(BigEndian(0u), token);
            await ReadExactAsync(stream, 1, token); // ClientInit: shared flag.

            var name = "Orrery test"u8.ToArray();
            var init = new List<byte>();
            init.AddRange(BigEndian(_desktopWidth));
            init.AddRange(BigEndian(_desktopHeight));
            init.AddRange([32, 24, 0, 1]);
            init.AddRange(BigEndian((ushort)255)); init.AddRange(BigEndian((ushort)255)); init.AddRange(BigEndian((ushort)255));
            init.AddRange([16, 8, 0, 0, 0, 0]);
            init.AddRange(BigEndian((uint)name.Length));
            init.AddRange(name);
            await stream.WriteAsync(init.ToArray(), token);
            _stream = stream;

            while (!token.IsCancellationRequested)
            {
                var type = (await ReadExactAsync(stream, 1, token))[0];
                switch (type)
                {
                    case 0: // SetPixelFormat
                        var pf = await ReadExactAsync(stream, 19, token);
                        (_bpp, _bigEndian) = (pf[3], pf[5] != 0);
                        (_redMax, _greenMax, _blueMax) = (BinaryPrimitives.ReadUInt16BigEndian(pf.AsSpan(7)), BinaryPrimitives.ReadUInt16BigEndian(pf.AsSpan(9)), BinaryPrimitives.ReadUInt16BigEndian(pf.AsSpan(11)));
                        (_redShift, _greenShift, _blueShift) = (pf[13], pf[14], pf[15]);
                        Log.Enqueue($"pixel format {_bpp}bpp r{_redShift} g{_greenShift} b{_blueShift}");
                        break;
                    case 2: // SetEncodings
                        var head = await ReadExactAsync(stream, 3, token);
                        var encodings = await ReadExactAsync(stream, 4 * BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1)), token);
                        for (int i = 0; i < encodings.Length; i += 4)
                        {
                            Encodings.Enqueue(BinaryPrimitives.ReadInt32BigEndian(encodings.AsSpan(i)));
                        }

                        break;
                    case 3: // FramebufferUpdateRequest: send the whole picture once; incremental requests get nothing.
                        var request = await ReadExactAsync(stream, 9, token);
                        if (request[0] == 0)
                        {
                            await WriteAsync(RawUpdate(), token);
                        }
                        break;
                    case 4: // KeyEvent
                        var key = await ReadExactAsync(stream, 7, token);
                        Keys.Enqueue((key[0] != 0, BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(3))));
                        break;
                    case 5: // PointerEvent
                        var pointer = await ReadExactAsync(stream, 5, token);
                        Pointer.Enqueue((pointer[0], BinaryPrimitives.ReadUInt16BigEndian(pointer.AsSpan(1)), BinaryPrimitives.ReadUInt16BigEndian(pointer.AsSpan(3))));
                        break;
                    case 6: // ClientCutText
                        var cut = await ReadExactAsync(stream, 7, token);
                        var text = await ReadExactAsync(stream, (int)BinaryPrimitives.ReadUInt32BigEndian(cut.AsSpan(3)), token);
                        ClientClipboard.Enqueue(Encoding.Latin1.GetString(text));
                        break;
                    default:
                        Log.Enqueue($"unexpected message {type}");
                        return;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or EndOfStreamException)
        {
            if (!_cts.IsCancellationRequested)
            {
                Log.Enqueue("client hung up");
            }
        }
    }

    byte[] RawUpdate()
    {
        int bytesPerPixel = _bpp / 8;
        int width = Math.Min(Width, (int)_desktopWidth), height = Math.Min(Height, (int)_desktopHeight);
        var message = new List<byte> { 0, 0 };
        message.AddRange(BigEndian((ushort)1));
        message.AddRange(BigEndian((ushort)0)); message.AddRange(BigEndian((ushort)0));
        message.AddRange(BigEndian((ushort)width)); message.AddRange(BigEndian((ushort)height));
        message.AddRange(BigEndian(0u)); // Raw

        var pixel = new byte[4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = PixelAt(x, y);
                uint value = (uint)(r * _redMax / 255) << _redShift | (uint)(g * _greenMax / 255) << _greenShift | (uint)(b * _blueMax / 255) << _blueShift;
                if (_bigEndian) BinaryPrimitives.WriteUInt32BigEndian(pixel, value << (32 - _bpp));
                else BinaryPrimitives.WriteUInt32LittleEndian(pixel, value);
                message.AddRange(pixel.AsSpan(0, bytesPerPixel).ToArray());
            }
        }

        return message.ToArray();
    }

    /// <summary>RFC 6143 §7.2.2: DES-encrypt the challenge with the password, each key byte bit-reversed.</summary>
    static byte[] VncAuthResponse(string password, byte[] challenge)
    {
        var key = new byte[8];
        var bytes = Encoding.ASCII.GetBytes(password);
        for (int i = 0; i < 8 && i < bytes.Length; i++)
        {
            byte b = bytes[i], reversed = 0;
            for (int bit = 0; bit < 8; bit++) reversed |= (byte)(((b >> bit) & 1) << (7 - bit));
            key[i] = reversed;
        }

        using var des = DES.Create();
        des.Key = key;
        return des.EncryptEcb(challenge, PaddingMode.None);
    }

    static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, token);
        return buffer;
    }

    static byte[] BigEndian(ushort value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); return b; }
    static byte[] BigEndian(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _serve;
        _cts.Dispose();
    }
}

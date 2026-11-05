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
/// It can offer other security types, or speak RFB 3.3 and name one, to see what a client sends for them.
/// </summary>
sealed class TestRfbServer : IAsyncDisposable
{
    public const int Width = 64, Height = 48;

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly string? _password;
    readonly ushort _desktopWidth, _desktopHeight;
    readonly byte[]? _securityTypes;
    readonly bool _rfb33;
    readonly bool _cursor;
    readonly List<byte> _received = [];
    byte[] _handshake = [];
    readonly CancellationTokenSource _cts = new();
    readonly Task _serve;

    // Server's own pixel format until the client sets one: 32 bpp little-endian, red at bit 16.
    int _bpp = 32, _redShift = 16, _greenShift = 8, _blueShift = 0, _redMax = 255, _greenMax = 255, _blueMax = 255;
    bool _bigEndian;

    /// <param name="securityTypes">The security types to offer instead of the one the password implies. Once the
    /// client picks one other than None or VNC Authentication, the server keeps what it sends and hangs up.</param>
    /// <param name="rfb33">Speak RFB 3.3, where the server names the first of the security types instead of offering them.</param>
    /// <param name="cursor">Send a cursor with the first picture (<see cref="CursorUpdate"/>).</param>
    public TestRfbServer(string? password, ushort desktopWidth = Width, ushort desktopHeight = Height,
        byte[]? securityTypes = null, bool rfb33 = false, bool cursor = false)
    {
        _cursor = cursor;
        _password = password;
        (_desktopWidth, _desktopHeight) = (desktopWidth, desktopHeight);
        (_securityTypes, _rfb33) = (securityTypes, rfb33);
        _listener.Start();
        _serve = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<(bool Down, uint Keysym)> Keys { get; } = new();
    public ConcurrentQueue<(byte Mask, int X, int Y)> Pointer { get; } = new();
    public ConcurrentQueue<string> Log { get; } = new();
    public ConcurrentQueue<string> ClientClipboard { get; } = new();
    public ConcurrentQueue<int> Encodings { get; } = new();

    /// <summary>Every byte the client has sent so far, in order.</summary>
    public byte[] Received { get { lock (_received) return [.. _received]; } }

    /// <summary>What the client sent before the security result: its version, its choice and any login response.</summary>
    public byte[] Handshake => Volatile.Read(ref _handshake);

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

            await stream.WriteAsync((_rfb33 ? "RFB 003.003\n"u8 : "RFB 003.008\n"u8).ToArray(), token);
            await ReadExactAsync(stream, 12, token);

            byte[] offered = _securityTypes ?? [_password is null ? (byte)1 : (byte)2];
            byte chosen;
            if (_rfb33)
            {
                chosen = offered[0];
                await stream.WriteAsync(BigEndian((uint)chosen), token);
            }
            else
            {
                await stream.WriteAsync(new[] { (byte)offered.Length }.Concat(offered).ToArray(), token);
                chosen = (await ReadExactAsync(stream, 1, token))[0];
            }

            Log.Enqueue($"security {chosen}");

            if (chosen is not (1 or 2))
            {
                await KeepUntilQuietAsync(stream, chosen, token);
                _handshake = Received;
                return;
            }

            if (chosen == 2)
            {
                var challenge = RandomNumberGenerator.GetBytes(16);
                await stream.WriteAsync(challenge, token);
                var response = await ReadExactAsync(stream, 16, token);
                _handshake = Received;
                if (!response.AsSpan().SequenceEqual(VncAuthResponse(_password!, challenge)))
                {
                    var reason = "Authentication failed"u8.ToArray();
                    await stream.WriteAsync(_rfb33 ? BigEndian(1u) : BigEndian(1u).Concat(BigEndian((uint)reason.Length)).Concat(reason).ToArray(), token);
                    Log.Enqueue("auth rejected");
                    return;
                }
            }
            else
            {
                _handshake = Received;
            }

            // RFB 3.3 sends no security result after None.
            if (chosen == 2 || !_rfb33)
            {
                await stream.WriteAsync(BigEndian(0u), token);
            }

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
        message.AddRange(BigEndian((ushort)(_cursor ? 2 : 1)));
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

        if (_cursor)
        {
            message.AddRange(CursorUpdate(bytesPerPixel));
        }

        return message.ToArray();
    }

    /// <summary>
    /// A Cursor pseudo-encoding rectangle (RFC 6143 §7.8.1): 2x1 with its hotspot on the right, a red pixel shown and
    /// a white one masked out.
    /// </summary>
    byte[] CursorUpdate(int bytesPerPixel)
    {
        var rect = new List<byte>();
        rect.AddRange(BigEndian((ushort)1)); rect.AddRange(BigEndian((ushort)0));
        rect.AddRange(BigEndian((ushort)2)); rect.AddRange(BigEndian((ushort)1));
        rect.AddRange(BigEndian(unchecked((uint)-239)));
        var pixel = new byte[4];
        foreach (var (r, g, b) in new[] { (255, 0, 0), (255, 255, 255) })
        {
            uint value = (uint)(r * _redMax / 255) << _redShift | (uint)(g * _greenMax / 255) << _greenShift | (uint)(b * _blueMax / 255) << _blueShift;
            if (_bigEndian) BinaryPrimitives.WriteUInt32BigEndian(pixel, value << (32 - _bpp));
            else BinaryPrimitives.WriteUInt32LittleEndian(pixel, value);
            rect.AddRange(pixel.AsSpan(0, bytesPerPixel).ToArray());
        }

        rect.Add(0b1000_0000);
        return rect.ToArray();
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

    async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, token);
        lock (_received) _received.AddRange(buffer);
        return buffer;
    }

    /// <summary>Keeps what the client sends for a security type this server doesn't speak, until it's quiet for a second.</summary>
    async Task KeepUntilQuietAsync(Stream stream, byte securityType, CancellationToken token)
    {
        // IntegratedSSH (24) waits for an SSH banner and then for a reply to each packet; play along, so a client
        // that speaks it gets as far as sending its login.
        bool ssh = securityType == 24;
        if (ssh)
        {
            await stream.WriteAsync("SSH-2.0-OrreryTest\r\n"u8.ToArray(), token);
        }

        var buffer = new byte[4096];
        while (true)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(token);
            quiet.CancelAfter(TimeSpan.FromSeconds(1));
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, quiet.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            lock (_received) _received.AddRange(buffer.AsSpan(0, read));
            if (ssh)
            {
                await stream.WriteAsync(new byte[16], token);
            }
        }
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

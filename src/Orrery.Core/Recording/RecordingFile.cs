using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;

namespace Orrery.Core.Recording;

public enum RecordingKind
{
    Terminal,
    Screen,
}

/// <summary>What an event in a recording is. The numbers are in the file.</summary>
public enum RecordingEventType : byte
{
    /// <summary>JSON <see cref="RecordingInfo"/>; the first event.</summary>
    Info = 1,

    /// <summary>Terminal output, as the session sent it.</summary>
    Output = 2,

    /// <summary>Two 32-bit sizes: columns and rows for a terminal, width and height for a screen.</summary>
    Resize = 3,

    /// <summary>UTF-8 text for the timeline: a disconnect, a reconnect.</summary>
    Marker = 4,

    /// <summary>A whole screen (<see cref="ScreenFrames"/>).</summary>
    KeyFrame = 5,

    /// <summary>Changed rectangles of a screen (<see cref="ScreenFrames"/>).</summary>
    Change = 6,

    /// <summary>Two 32-bit coordinates: where the pointer is on the screen.</summary>
    Pointer = 7,

    /// <summary>The recording was closed; the last event.</summary>
    End = 8,

    /// <summary>Sound the session played: sample rate (32 bits), channels (16 bits), then 16-bit PCM (<see cref="RecordingFile.AudioPayload"/>).</summary>
    Audio = 9,
}

/// <summary>What a recording is of, kept inside its encrypted part.</summary>
/// <param name="ColorCorrection">
/// The connection's "Color correction": false when it was off, so the recording plays in its own colors whatever the
/// viewer has set. Null where it doesn't apply (terminals) and in recordings made before it was kept.
/// </param>
/// <param name="CorrectedFor">
/// The color correction the pictures were recorded with already, as a web page's are (its screencast is of the page
/// as shown); null when they're the colors as they were.
/// </param>
/// <param name="CorrectedStrength">How strong that correction was, in percent.</param>
public sealed record RecordingInfo(string Name, string Protocol, DateTimeOffset Started, bool? ColorCorrection = null,
    Settings.ColorVision? CorrectedFor = null, int? CorrectedStrength = null);

/// <summary>An event read back, with where it starts so playing can come back to it.</summary>
public sealed record RecordedEvent(RecordingEventType Type, TimeSpan Time, byte[] Payload, EventPosition Position);

/// <summary>Where an event starts: its chunk's place in the file, the chunk's number and the offset inside it.</summary>
public readonly record struct EventPosition(long ChunkOffset, long ChunkIndex, int Offset);

/// <summary>How reading a recording ended.</summary>
public enum RecordingEnding
{
    /// <summary>Not read to the end yet.</summary>
    Unknown,

    /// <summary>It was closed properly.</summary>
    Closed,

    /// <summary>It stops without being closed, most likely because Orrery stopped while recording.</summary>
    Unclosed,

    /// <summary>A part failed its check: the rest can't be read.</summary>
    Damaged,
}

/// <summary>The private key isn't the one this recording was sealed to.</summary>
public sealed class RecordingKeyException(string message) : Exception(message);

/// <summary>
/// The not-secret start of a recording: what's needed to open it, and nothing about the host (design §3.2).
/// </summary>
public sealed record RecordingHeader(
    string Format,
    int Version,
    RecordingKind Kind,
    string KeyId,
    [property: JsonPropertyName("ephemeral")] string EphemeralPublicKey,
    string KeyNonce,
    string SealedKey,
    string NoncePrefix);

/// <summary>
/// The <c>.orrec</c> format (docs/design-session-recording.md §3.2):
/// <c>"ORREC" 0x01</c>, a 32-bit header length, the header's JSON, then chunks of up to <see cref="ChunkSize"/>
/// bytes of events, each sealed with XChaCha20-Poly1305 under the file key: a flags byte (1 marks the last), a 32-bit
/// length and the ciphertext. A chunk's nonce is the file's prefix and the chunk's number, and its associated data is
/// a hash of everything before the first chunk, the chunk's number and its flags, so chunks can't be reordered,
/// dropped, moved between files or have their last mark changed. Events run across chunks as one stream: a type
/// byte, a 32-bit time in milliseconds, a 32-bit length and the payload. All numbers are little-endian.
/// </summary>
public static class RecordingFile
{
    public const string Extension = ".orrec";
    public const string FormatName = "orrery-recording";
    public const int Version = 1;
    public const int ChunkSize = 64 * 1024;
    public const int MaxHeaderSize = 64 * 1024;

    /// <summary>The largest event read back: a key frame of the largest screen, compressed, fits well inside.</summary>
    public const int MaxEventSize = 64 * 1024 * 1024;

    internal const int EventHeaderSize = 9;
    internal const byte LastFlag = 1;
    internal const int NoncePrefixSize = 16;
    internal static readonly byte[] Magic = "ORREC\u0001"u8.ToArray();
    internal static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static byte[] Nonce(byte[] prefix, long index)
    {
        var nonce = new byte[Aead.NonceSize];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(NoncePrefixSize), index);
        return nonce;
    }

    internal static byte[] AssociatedData(byte[] preambleHash, long index, byte flags)
    {
        var data = new byte[preambleHash.Length + 9];
        preambleHash.CopyTo(data, 0);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(preambleHash.Length), index);
        data[^1] = flags;
        return data;
    }

    /// <summary>Two 32-bit numbers, as a resize or pointer event holds them.</summary>
    public static byte[] Pair(int first, int second)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, first);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), second);
        return payload;
    }

    /// <summary>The two numbers of a resize or pointer event, or null if it doesn't hold two.</summary>
    public static (int First, int Second)? ReadPair(byte[] payload) =>
        payload.Length == 8 ? (BinaryPrimitives.ReadInt32LittleEndian(payload), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4))) : null;

    /// <summary>An <see cref="RecordingEventType.Audio"/> event's payload.</summary>
    public static byte[] AudioPayload(int sampleRate, int channels, ReadOnlySpan<byte> pcm)
    {
        var payload = new byte[6 + pcm.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)channels);
        pcm.CopyTo(payload.AsSpan(6));
        return payload;
    }

    /// <summary>
    /// The sound of an <see cref="RecordingEventType.Audio"/> event, or null if it doesn't hold whole 16-bit frames at a
    /// rate and channel count that can be played.
    /// </summary>
    public static (int SampleRate, int Channels, ReadOnlyMemory<byte> Pcm)? ReadAudio(byte[] payload)
    {
        if (payload.Length < 6)
        {
            return null;
        }

        int rate = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4));
        int frame = channels * 2;
        if (rate is < 4_000 or > 192_000 || channels is < 1 or > 8 || (payload.Length - 6) % frame != 0)
        {
            return null;
        }

        return (rate, channels, payload.AsMemory(6));
    }

    public static byte[] InfoPayload(RecordingInfo info) => JsonSerializer.SerializeToUtf8Bytes(info, JsonOptions);

    public static RecordingInfo? ReadInfo(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<RecordingInfo>(payload, JsonOptions) is { Name: not null, Protocol: not null } info ? info : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Writes a recording. Events are buffered and sealed a chunk at a time; <see cref="Flush"/> seals what's buffered
/// so a crash loses only what came after. Safe to call from several threads.
/// </summary>
public sealed class RecordingWriter : IDisposable
{
    readonly FileStream _file;
    readonly Key _key;
    readonly byte[] _noncePrefix;
    readonly byte[] _preambleHash;
    readonly byte[] _buffer = new byte[RecordingFile.ChunkSize];
    readonly object _lock = new();
    int _buffered;
    long _chunkIndex;
    bool _completed;
    bool _disposed;

    RecordingWriter(FileStream file, Key key, byte[] noncePrefix, byte[] preambleHash)
    {
        _file = file;
        _key = key;
        _noncePrefix = noncePrefix;
        _preambleHash = preambleHash;
    }

    public string Path => _file.Name;

    /// <summary>Bytes in the file so far, not counting what's still buffered.</summary>
    public long Length
    {
        get
        {
            lock (_lock)
            {
                return _disposed ? 0 : _file.Length;
            }
        }
    }

    /// <summary>Makes a new recording at <paramref name="path"/>, which mustn't exist, sealed to <paramref name="publicKey"/>.</summary>
    public static RecordingWriter Create(string path, RecordingKind kind, byte[] publicKey)
    {
        var fileKey = RecordingKeys.NewFileKey();
        try
        {
            var sealedKey = RecordingKeys.Seal(publicKey, fileKey);
            var noncePrefix = RandomNumberGenerator.GetBytes(RecordingFile.NoncePrefixSize);
            var header = new RecordingHeader(RecordingFile.FormatName, RecordingFile.Version, kind, RecordingKeys.KeyId(publicKey),
                Convert.ToBase64String(sealedKey.EphemeralPublicKey), Convert.ToBase64String(sealedKey.Nonce),
                Convert.ToBase64String(sealedKey.Ciphertext), Convert.ToBase64String(noncePrefix));
            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, RecordingFile.JsonOptions);
            var preamble = new byte[RecordingFile.Magic.Length + 4 + headerBytes.Length];
            RecordingFile.Magic.CopyTo(preamble, 0);
            BinaryPrimitives.WriteInt32LittleEndian(preamble.AsSpan(RecordingFile.Magic.Length), headerBytes.Length);
            headerBytes.CopyTo(preamble, RecordingFile.Magic.Length + 4);

            // Shared for reading, so the list can show it while it's written, but not for deleting.
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            try
            {
                file.Write(preamble);
                file.Flush();
                var key = Key.Import(RecordingFile.Aead, fileKey, KeyBlobFormat.RawSymmetricKey);
                return new RecordingWriter(file, key, noncePrefix, SHA256.HashData(preamble));
            }
            catch
            {
                file.Dispose();
                File.Delete(path);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    public void Append(RecordingEventType type, TimeSpan time, ReadOnlySpan<byte> payload)
    {
        Span<byte> head = stackalloc byte[RecordingFile.EventHeaderSize];
        head[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(head[1..], (uint)Math.Clamp(time.TotalMilliseconds, 0, uint.MaxValue));
        BinaryPrimitives.WriteInt32LittleEndian(head[5..], payload.Length);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed || _completed, this);
            Buffer(head);
            Buffer(payload);
        }
    }

    /// <summary>Seals what's buffered and writes it to disk.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed || _completed)
            {
                return;
            }

            if (_buffered > 0)
            {
                Seal(last: false);
            }

            _file.Flush(flushToDisk: false);
        }
    }

    /// <summary>Writes the end, marks the last chunk, and closes the file.</summary>
    public void Complete(TimeSpan time)
    {
        lock (_lock)
        {
            if (_disposed || _completed)
            {
                return;
            }
        }

        Append(RecordingEventType.End, time, []);
        lock (_lock)
        {
            Seal(last: true);
            _completed = true;
            _file.Flush(flushToDisk: true);
        }

        Dispose();
    }

    // Caller holds _lock.
    void Buffer(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int take = Math.Min(data.Length, _buffer.Length - _buffered);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
            if (_buffered == _buffer.Length)
            {
                Seal(last: false);
            }
        }
    }

    // Caller holds _lock.
    void Seal(bool last)
    {
        byte flags = last ? RecordingFile.LastFlag : (byte)0;
        var ciphertext = RecordingFile.Aead.Encrypt(_key, RecordingFile.Nonce(_noncePrefix, _chunkIndex),
            RecordingFile.AssociatedData(_preambleHash, _chunkIndex, flags), _buffer.AsSpan(0, _buffered));
        Span<byte> head = stackalloc byte[5];
        head[0] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(head[1..], ciphertext.Length);
        _file.Write(head);
        _file.Write(ciphertext);
        CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _buffered));
        _buffered = 0;
        _chunkIndex++;
    }

    /// <summary>Closes the file. One not completed is left as a recording that ended without being closed.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (!_completed && _buffered > 0)
            {
                try
                {
                    Seal(last: false);
                }
                catch (IOException)
                {
                    // The disk is full or gone: what's on it already still plays.
                }
            }

            _disposed = true;
            _key.Dispose();
            try
            {
                _file.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Reads a recording back, checking each chunk before using any of it.</summary>
public sealed class RecordingReader : IDisposable
{
    readonly FileStream _file;
    readonly Key _key;
    readonly byte[] _noncePrefix;
    readonly byte[] _preambleHash;
    readonly long _firstChunk;

    RecordingReader(FileStream file, RecordingHeader header, Key key, byte[] noncePrefix, byte[] preambleHash, long firstChunk)
    {
        _file = file;
        Header = header;
        _key = key;
        _noncePrefix = noncePrefix;
        _preambleHash = preambleHash;
        _firstChunk = firstChunk;
    }

    public RecordingHeader Header { get; }

    /// <summary>How the last read through <see cref="Events"/> ended.</summary>
    public RecordingEnding Ending { get; private set; }

    /// <summary>The header alone, which needs no key.</summary>
    /// <exception cref="InvalidDataException">It isn't a recording, or one this version reads.</exception>
    public static RecordingHeader ReadHeader(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadPreamble(file).Header;
    }

    /// <exception cref="InvalidDataException">It isn't a recording, or one this version reads.</exception>
    /// <exception cref="RecordingKeyException">It was sealed to a different key.</exception>
    public static RecordingReader Open(string path, byte[] privateKey)
    {
        // Shared for writing too, so a recording still being made can be played up to where it's got.
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var (header, preambleHash) = ReadPreamble(file);
            if (header.KeyId != RecordingKeys.KeyId(RecordingKeys.PublicKeyOf(privateKey)))
            {
                throw new RecordingKeyException("This recording was made with a different recordings key, so it can't be played here.");
            }

            var sealedKey = new SealedFileKey(Decode(header.EphemeralPublicKey), Decode(header.KeyNonce), Decode(header.SealedKey));
            var fileKey = RecordingKeys.Open(privateKey, sealedKey)
                ?? throw new InvalidDataException("This recording's key is damaged.");
            try
            {
                var key = Key.Import(RecordingFile.Aead, fileKey, KeyBlobFormat.RawSymmetricKey);
                return new RecordingReader(file, header, key, Decode(header.NoncePrefix), preambleHash, file.Position);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileKey);
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    static (RecordingHeader Header, byte[] PreambleHash) ReadPreamble(FileStream file)
    {
        const string NotOne = "This isn't an Orrery recording, or it's from a newer version of Orrery.";
        var start = new byte[RecordingFile.Magic.Length + 4];
        if (file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) != start.Length
            || !start.AsSpan(0, RecordingFile.Magic.Length).SequenceEqual(RecordingFile.Magic))
        {
            throw new InvalidDataException(NotOne);
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(start.AsSpan(RecordingFile.Magic.Length));
        if (length is <= 0 or > RecordingFile.MaxHeaderSize)
        {
            throw new InvalidDataException(NotOne);
        }

        var headerBytes = new byte[length];
        if (file.ReadAtLeast(headerBytes, length, throwOnEndOfStream: false) != length)
        {
            throw new InvalidDataException(NotOne);
        }

        RecordingHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<RecordingHeader>(headerBytes, RecordingFile.JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidDataException(NotOne);
        }

        if (header is not { Format: RecordingFile.FormatName, Version: RecordingFile.Version }
            || !Enum.IsDefined(header.Kind) || header.KeyId is null
            || DecodedLength(header.EphemeralPublicKey) != RecordingKeys.KeySize
            || DecodedLength(header.KeyNonce) != RecordingFile.Aead.NonceSize
            || DecodedLength(header.SealedKey) != RecordingKeys.KeySize + RecordingFile.Aead.TagSize
            || DecodedLength(header.NoncePrefix) != RecordingFile.NoncePrefixSize)
        {
            throw new InvalidDataException(NotOne);
        }

        var preamble = new byte[start.Length + length];
        start.CopyTo(preamble, 0);
        headerBytes.CopyTo(preamble, start.Length);
        return (header, SHA256.HashData(preamble));
    }

    /// <summary>
    /// The events from <paramref name="from"/> (or the start) on. Stops at the end, or at the first chunk that fails
    /// its check, and sets <see cref="Ending"/> to say which.
    /// </summary>
    public IEnumerable<RecordedEvent> Events(EventPosition? from = null)
    {
        var cursor = new ChunkCursor(this, from?.ChunkOffset ?? _firstChunk, from?.ChunkIndex ?? 0);
        Ending = RecordingEnding.Unknown;
        if (!cursor.Next())
        {
            Ending = cursor.Ending;
            yield break;
        }

        if (from is { Offset: var skip } && !cursor.Skip(skip))
        {
            Ending = cursor.Ending;
            yield break;
        }

        var head = new byte[RecordingFile.EventHeaderSize];
        while (true)
        {
            if (cursor.AtEnd)
            {
                Ending = cursor.Ending;
                yield break;
            }

            var position = cursor.Position;
            if (!cursor.Read(head))
            {
                Ending = cursor.Ending == RecordingEnding.Closed ? RecordingEnding.Damaged : cursor.Ending;
                yield break;
            }

            var type = (RecordingEventType)head[0];
            var time = TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1)));
            int length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(5));
            if (!Enum.IsDefined(type) || length is < 0 or > RecordingFile.MaxEventSize)
            {
                Ending = RecordingEnding.Damaged;
                yield break;
            }

            var payload = new byte[length];
            if (!cursor.Read(payload))
            {
                Ending = cursor.Ending == RecordingEnding.Closed ? RecordingEnding.Damaged : cursor.Ending;
                yield break;
            }

            yield return new RecordedEvent(type, time, payload, position);
            if (type == RecordingEventType.End)
            {
                Ending = cursor.IsLast && cursor.AtEnd ? RecordingEnding.Closed : RecordingEnding.Damaged;
                yield break;
            }
        }
    }

    public void Dispose()
    {
        _key.Dispose();
        _file.Dispose();
    }

    static byte[] Decode(string base64) => Convert.FromBase64String(base64);

    static int DecodedLength(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return -1;
        }

        var buffer = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, buffer, out int written) ? written : -1;
    }

    /// <summary>Reads the chunks in order from one place, as one stream of bytes.</summary>
    sealed class ChunkCursor(RecordingReader reader, long offset, long index)
    {
        byte[] _plain = [];
        int _at;
        long _chunkOffset;
        long _nextOffset = offset;
        long _index = index - 1;

        public RecordingEnding Ending { get; private set; } = RecordingEnding.Unknown;
        public bool IsLast { get; private set; }

        /// <summary>Nothing more to read: this chunk is used up and there's no next one (or it can't be read).</summary>
        public bool AtEnd => _at >= _plain.Length && (IsLast || !Next());

        public EventPosition Position => new(_chunkOffset, _index, _at);

        /// <summary>Moves to the next chunk; false at the end of the file or a bad chunk, with <see cref="Ending"/> set.</summary>
        public bool Next()
        {
            if (IsLast || Ending != RecordingEnding.Unknown)
            {
                return false;
            }

            var file = reader._file;
            file.Position = _nextOffset;
            Span<byte> head = stackalloc byte[5];
            int got = file.ReadAtLeast(head, 5, throwOnEndOfStream: false);
            if (got == 0)
            {
                Ending = RecordingEnding.Unclosed;
                return false;
            }

            if (got < 5)
            {
                Ending = RecordingEnding.Unclosed;
                return false;
            }

            byte flags = head[0];
            int length = BinaryPrimitives.ReadInt32LittleEndian(head[1..]);
            if ((flags & ~RecordingFile.LastFlag) != 0 || length < RecordingFile.Aead.TagSize || length > RecordingFile.ChunkSize + RecordingFile.Aead.TagSize)
            {
                Ending = RecordingEnding.Damaged;
                return false;
            }

            var ciphertext = new byte[length];
            if (file.ReadAtLeast(ciphertext, length, throwOnEndOfStream: false) != length)
            {
                // Cut off while it was being written.
                Ending = RecordingEnding.Unclosed;
                return false;
            }

            long index = _index + 1;
            var plain = RecordingFile.Aead.Decrypt(reader._key, RecordingFile.Nonce(reader._noncePrefix, index),
                RecordingFile.AssociatedData(reader._preambleHash, index, flags), ciphertext);
            if (plain is null)
            {
                Ending = RecordingEnding.Damaged;
                return false;
            }

            _chunkOffset = _nextOffset;
            _nextOffset = file.Position;
            _index = index;
            _plain = plain;
            _at = 0;
            IsLast = (flags & RecordingFile.LastFlag) != 0;
            if (IsLast)
            {
                // Anything after the last chunk wasn't written by Orrery.
                Ending = file.Position == file.Length ? RecordingEnding.Closed : RecordingEnding.Damaged;
            }

            return true;
        }

        public bool Skip(int count)
        {
            if (count < 0 || count > _plain.Length)
            {
                Ending = RecordingEnding.Damaged;
                return false;
            }

            _at = count;
            return true;
        }

        public bool Read(Span<byte> destination)
        {
            while (destination.Length > 0)
            {
                if (_at >= _plain.Length && !Next())
                {
                    return false;
                }

                int take = Math.Min(destination.Length, _plain.Length - _at);
                _plain.AsSpan(_at, take).CopyTo(destination);
                _at += take;
                destination = destination[take..];
            }

            return true;
        }
    }
}

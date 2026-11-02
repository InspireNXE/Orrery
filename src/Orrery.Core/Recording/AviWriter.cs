using System.Buffers.Binary;
using System.Text;

namespace Orrery.Core.Recording;

/// <summary>
/// A Motion-JPEG AVI file (design §3.4): RIFF <c>AVI </c> with an <c>hdrl</c> list, a <c>movi</c> list of JPEG frames and
/// an <c>idx1</c> index. A frame that repeats the one before is written as an empty chunk, which players hold the last
/// picture through. Plain AVI stops at 1 GB of frames, so the export starts another part there
/// (<see cref="IsFull"/>). The stream must be seekable: the sizes and counts are filled in at the end.
/// </summary>
public sealed class AviWriter : IDisposable
{
    /// <summary>Where a part is full, well inside AVI 1.0's limits.</summary>
    public const long MaxPartSize = 1L << 30;

    const int AviIfKeyFrame = 0x10;
    const int AvifHasIndex = 0x10;

    readonly Stream _stream;
    readonly int _width, _height, _framesPerSecond;
    readonly List<(int Offset, int Size, bool Key)> _index = [];
    readonly long _riffSizeAt, _totalFramesAt, _lengthAt, _moviSizeAt, _moviStart, _avihBufferAt, _strhBufferAt;
    int _largest;
    bool _finished;

    public AviWriter(Stream stream, int width, int height, int framesPerSecond)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("An AVI file needs a stream it can seek in.", nameof(stream));
        }

        _stream = stream;
        _width = width;
        _height = height;
        _framesPerSecond = Math.Max(1, framesPerSecond);

        _riffSizeAt = Fourcc("RIFF");
        WriteInt(0);
        Fourcc("AVI ");

        long hdrlSizeAt = List("hdrl");
        Fourcc("avih");
        WriteInt(56);
        WriteInt(1_000_000 / _framesPerSecond); // microseconds a frame
        WriteInt(0);                             // max bytes a second
        WriteInt(0);                             // padding granularity
        WriteInt(AvifHasIndex);
        _totalFramesAt = _stream.Position;
        WriteInt(0);                             // total frames
        WriteInt(0);                             // initial frames
        WriteInt(1);                             // streams
        _avihBufferAt = _stream.Position;
        WriteInt(0);                             // suggested buffer size
        WriteInt(width);
        WriteInt(height);
        for (int i = 0; i < 4; i++)
        {
            WriteInt(0);
        }

        long strlSizeAt = List("strl");
        Fourcc("strh");
        WriteInt(56);
        Fourcc("vids");
        Fourcc("MJPG");
        WriteInt(0);                             // flags
        WriteShort(0);                           // priority
        WriteShort(0);                           // language
        WriteInt(0);                             // initial frames
        WriteInt(1);                             // scale
        WriteInt(_framesPerSecond);              // rate: rate / scale frames a second
        WriteInt(0);                             // start
        _lengthAt = _stream.Position;
        WriteInt(0);                             // length, in frames
        _strhBufferAt = _stream.Position;
        WriteInt(0);                             // suggested buffer size
        WriteInt(-1);                            // quality: the default
        WriteInt(0);                             // sample size: varies
        WriteShort(0);
        WriteShort(0);
        WriteShort((short)Math.Min(width, short.MaxValue));
        WriteShort((short)Math.Min(height, short.MaxValue));

        Fourcc("strf");
        WriteInt(40);
        WriteInt(40);                            // BITMAPINFOHEADER size
        WriteInt(width);
        WriteInt(height);
        WriteShort(1);                           // planes
        WriteShort(24);                          // bits a pixel
        Fourcc("MJPG");
        WriteInt(width * height * 3);
        WriteInt(0);
        WriteInt(0);
        WriteInt(0);
        WriteInt(0);
        EndList(strlSizeAt);
        EndList(hdrlSizeAt);

        _moviSizeAt = List("movi");
        _moviStart = _stream.Position - 4; // idx1 offsets count from the "movi" fourcc
    }

    public int Frames => _index.Count;

    /// <summary>The part has reached <see cref="MaxPartSize"/>: finish it and start another.</summary>
    public bool IsFull => _stream.Position >= MaxPartSize;

    /// <summary>Adds a frame: a JPEG, or with <paramref name="repeat"/> an empty one for "the same as the last frame".</summary>
    public void AddFrame(ReadOnlySpan<byte> jpeg, bool repeat = false)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var data = repeat ? [] : jpeg;
        int offset = (int)(_stream.Position - _moviStart);
        Fourcc("00dc");
        WriteInt(data.Length);
        _stream.Write(data);
        if ((data.Length & 1) != 0)
        {
            _stream.WriteByte(0);
        }

        _index.Add((offset, data.Length, !repeat));
        _largest = Math.Max(_largest, data.Length);
    }

    /// <summary>Writes the index and fills in the sizes. The stream is left open.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        EndList(_moviSizeAt);
        Fourcc("idx1");
        WriteInt(_index.Count * 16);
        foreach (var (offset, size, key) in _index)
        {
            Fourcc("00dc");
            WriteInt(key ? AviIfKeyFrame : 0);
            WriteInt(offset);
            WriteInt(size);
        }

        long end = _stream.Position;
        Patch(_riffSizeAt, (int)(end - _riffSizeAt - 4));
        Patch(_totalFramesAt, _index.Count);
        Patch(_lengthAt, _index.Count);
        Patch(_avihBufferAt, _largest + 8);
        Patch(_strhBufferAt, _largest + 8);
        _stream.Position = end;
        _stream.Flush();
    }

    public void Dispose() => Finish();

    // Writes "LIST", room for its size and its type; returns where the size goes.
    long List(string type)
    {
        Fourcc("LIST");
        long at = _stream.Position;
        WriteInt(0);
        Fourcc(type);
        return at;
    }

    void EndList(long sizeAt) => Patch(sizeAt, (int)(_stream.Position - sizeAt - 4));

    void Patch(long at, int value)
    {
        long back = _stream.Position;
        _stream.Position = at;
        WriteInt(value);
        _stream.Position = back;
    }

    // Returns where the fourcc ends, which for "RIFF" is where its size goes.
    long Fourcc(string code)
    {
        _stream.Write(Encoding.ASCII.GetBytes(code));
        return _stream.Position;
    }

    void WriteInt(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    void WriteShort(short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        _stream.Write(bytes);
    }
}

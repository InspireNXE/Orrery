using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Core.Recording;

/// <summary>A video codec Windows itself can encode, through one of its own encoders or the graphics card's.</summary>
/// <param name="HardwareOnly">Only the graphics card has an encoder for it, which may not take every size.</param>
public sealed record SystemCodec(string Name, Guid Subtype, bool HardwareOnly);

/// <summary>Why Windows couldn't make the video, in words for the user.</summary>
public sealed class VideoEncoderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// MP4 export through Windows' own encoders (docs/design-session-recording.md §3.4), so it needs nothing installed:
/// Media Foundation's sink writer, given NV12 pictures. Windows pays for the H.264 and H.265 licenses of the encoders
/// it ships. Objects are raw COM pointers, and methods are called by their vtable slot as declared in the Windows
/// SDK's mfobjects.h and mfreadwrite.h, as <c>WinRt</c> does: slots 0–2 are IUnknown, and IMFAttributes' own start at 3.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class MediaFoundation
{
    internal const uint Version = 0x0002_0070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    const uint ListAll = 0x1 | 0x2 | 0x40;     // MFT_ENUM_FLAG_SYNCMFT | ASYNCMFT | SORTANDFILTER
    const uint ListHardware = 0x4 | 0x40;      // MFT_ENUM_FLAG_HARDWARE | SORTANDFILTER

    internal static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    internal static readonly Guid MajorVideo = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Nv12Subtype = FourCc("NV12");
    internal static readonly Guid MajorAudio = new("73647561-0000-0010-8000-00aa00389b71");

    // DEFINE_MEDIATYPE_GUID with WAVE_FORMAT_PCM (1) and WAVE_FORMAT_MPEG_HEAAC (0x1610), from mfapi.h and mmreg.h.
    internal static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid AacSubtype = new("00001610-0000-0010-8000-00aa00389b71");

    // In order of preference.
    static readonly (string Name, Guid Subtype)[] Known =
    [
        ("H.264", FourCc("H264")),
        ("H.265", FourCc("HEVC")),
        ("AV1", FourCc("AV01")),
    ];

    /// <summary>The codecs this Windows can encode now; none on a Windows without Media Foundation (the N editions).</summary>
    public static IReadOnlyList<SystemCodec> Codecs()
    {
        var found = new List<SystemCodec>();
        try
        {
            Check(MFStartup(Version, 0));
            try
            {
                foreach (var (name, subtype) in Known)
                {
                    if (Count(subtype, ListAll) > 0)
                    {
                        found.Add(new SystemCodec(name, subtype, HardwareOnly: false));
                    }
                    else if (Count(subtype, ListHardware) > 0)
                    {
                        found.Add(new SystemCodec(name, subtype, HardwareOnly: true));
                    }
                }
            }
            finally
            {
                MFShutdown();
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return [];
        }

        return found;
    }

    static int Count(Guid subtype, uint flags)
    {
        var output = new TypeInfo { Major = MajorVideo, Sub = subtype };
        var category = VideoEncoderCategory;
        IntPtr* activates = null;
        uint count = 0;
        if (MFTEnumEx(category, flags, null, &output, &activates, &count) < 0 || activates is null)
        {
            return 0;
        }

        for (uint i = 0; i < count; i++)
        {
            Release(activates[i]);
        }

        Marshal.FreeCoTaskMem((IntPtr)activates);
        return (int)count;
    }

    /// <summary>
    /// Exports a screen recording as MP4 with <paramref name="codec"/>, at the size of its first picture rounded up to
    /// even sides, or to 16-pixel blocks for a graphics card. A canceled or failed export deletes what it wrote.
    /// </summary>
    /// <param name="progress">Told how far it has got now and then, from this thread.</param>
    /// <exception cref="VideoEncoderException">Windows couldn't make the video.</exception>
    /// <param name="recolor">Passed to <see cref="ScreenExport.Frames"/>.</param>
    public static void Export(RecordingReader reader, SystemCodec codec, string output, int framesPerSecond, bool skipIdle,
        Action<TimeSpan>? progress = null, CancellationToken cancel = default, Action<byte[]>? recolor = null)
    {
        if (ScreenExport.FirstSize(reader) is not var (first, second))
        {
            throw new InvalidDataException("This recording has no picture in it.");
        }

        // A graphics card's encoder may round the size up to whole 16-pixel blocks and show what it fills them with, so
        // for those the picture is padded with black to the blocks first.
        var (width, height) = codec.HardwareOnly ? ((first + 15) & ~15, (second + 15) & ~15) : Nv12.EvenSize(first, second);
        bool done = false;
        try
        {
            var sound = SoundFormat.Of(reader, cancel);
            using var writer = new MediaFoundationVideoWriter(output, codec, width, height, framesPerSecond, sound);
            int index = 0;
            foreach (var frame in ScreenExport.Frames(reader, framesPerSecond, width, height, skipIdle, cancel, sound, recolor: recolor))
            {
                writer.Write(frame.Rgb, frame.Changed);
                writer.WriteSound(frame.Sound);
                if (++index % 25 == 0)
                {
                    progress?.Invoke(frame.Time);
                }
            }

            writer.Finish();
            done = true;
        }
        finally
        {
            if (!done)
            {
                try
                {
                    File.Delete(output);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>A Media Foundation subtype from its four-character code, as DEFINE_MEDIATYPE_GUID makes them.</summary>
    internal static Guid FourCc(string code) =>
        new(code[0] | code[1] << 8 | code[2] << 16 | code[3] << 24, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    // ---- COM by vtable slot ----

    internal static void* Method(IntPtr obj, int slot) => (*(void***)obj)[slot];

    internal static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Method(obj, 2))(obj);
        }
    }

    internal static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    // IMFAttributes: SetUINT32 21, SetUINT64 22, SetGUID 24.
    internal static void SetUInt32(IntPtr attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Method(attributes, 21))(attributes, &key, value));

    internal static void SetUInt64(IntPtr attributes, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Method(attributes, 22))(attributes, &key, value));

    internal static void SetGuid(IntPtr attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Method(attributes, 24))(attributes, &key, &value));

    [StructLayout(LayoutKind.Sequential)]
    internal struct TypeInfo
    {
        public Guid Major;
        public Guid Sub;
    }

    [DllImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, IntPtr** activates, uint* count);

    [DllImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateAttributes(IntPtr* attributes, uint initialSize);

    [DllImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateMediaType(IntPtr* mediaType);

    [DllImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateSample(IntPtr* sample);

    [DllImport("mfplat.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateMemoryBuffer(uint maxLength, IntPtr* buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, IntPtr* sinkWriter);

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void CoUninitialize();
}

/// <summary>
/// Writes an MP4 file through Media Foundation's sink writer, one RGB picture at a time at a steady frame rate. Use it
/// on one thread from start to end: it joins that thread to the multithreaded apartment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationVideoWriter : IDisposable
{
    // Attribute keys, from mfapi.h and mfreadwrite.h.
    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    static readonly Guid Primaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    static readonly Guid ContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    static readonly Guid ContainerMpeg4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    static readonly Guid AudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    static readonly Guid AudioBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    /// <summary>AAC at 192 kbit/s (24,000 bytes a second), the highest rate the Windows AAC encoder takes.</summary>
    const uint AacBytesPerSecond = 24_000;

    const uint Progressive = 2, Bt709Matrix = 1, Range16To235 = 2, Bt709Primaries = 2, Bt709Transfer = 5;

    readonly int _width, _height, _framesPerSecond;
    readonly byte[] _nv12;
    bool _comInitialized;
    readonly string _codecName;
    IntPtr _writer;
    uint _stream, _soundStream;
    long _frames, _soundFrames;
    readonly SoundFormat? _sound;
    bool _started, _finished;

    /// <param name="width">Even, as NV12 needs: see <see cref="Nv12.EvenSize"/>.</param>
    /// <param name="sound">With a format, an AAC sound track too, written with <see cref="WriteSound"/>.</param>
    public MediaFoundationVideoWriter(string path, SystemCodec codec, int width, int height, int framesPerSecond, SoundFormat? sound = null)
    {
        if ((width | height) % 2 != 0 || width <= 0 || height <= 0 || framesPerSecond <= 0)
        {
            throw new ArgumentException("The video needs an even width and height and a frame rate.");
        }

        _width = width;
        _height = height;
        _framesPerSecond = framesPerSecond;
        _codecName = codec.Name;
        _sound = sound;
        _nv12 = new byte[Nv12.Size(width, height)];

        // S_OK or S_FALSE join the apartment and must be undone; a thread already in the single-threaded one works too.
        _comInitialized = MediaFoundation.CoInitializeEx(IntPtr.Zero, 0) >= 0;
        try
        {
            Native("start Media Foundation", MediaFoundation.MFStartup(MediaFoundation.Version, 0));
            _started = true;
            Open(path, codec);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void Open(string path, SystemCodec codec)
    {
        IntPtr attributes = 0, output = 0, input = 0, writer = 0;
        try
        {
            try
            {
                MediaFoundation.Check(MediaFoundation.MFCreateAttributes(&attributes, 2));
                // The graphics card's encoders only when Windows has none of its own: with one, the sink writer can hang
                // in Finalize waiting for frames it never hands back.
                MediaFoundation.SetUInt32(attributes, EnableHardwareTransforms, codec.HardwareOnly ? 1u : 0u);
                MediaFoundation.SetGuid(attributes, ContainerType, ContainerMpeg4);
                MediaFoundation.Check(MediaFoundation.MFCreateSinkWriterFromURL(path, 0, attributes, &writer));
                _writer = writer;
            }
            catch (Exception e) when (IsNative(e))
            {
                throw Failed("set up the MP4 file", e);
            }

            // Screens are mostly still and full of fine text: about a quarter of a bit a pixel, never under 1 Mbit/s.
            long bitrate = Math.Clamp((long)_width * _height * Math.Max(_framesPerSecond, 5) / 4, 1_000_000, 40_000_000);
            try
            {
                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(&output));
                Describe(output, codec.Subtype);
                MediaFoundation.SetUInt32(output, AverageBitrate, (uint)bitrate);
                uint stream;

                // IMFSinkWriter: AddStream 3, SetInputMediaType 4, BeginWriting 5.
                MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)MediaFoundation.Method(_writer, 3))(_writer, output, &stream));
                _stream = stream;

                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(&input));
                Describe(input, MediaFoundation.Nv12Subtype);
                MediaFoundation.SetUInt32(input, DefaultStride, (uint)_width);
                MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)MediaFoundation.Method(_writer, 4))(_writer, _stream, input, 0));
            }
            catch (Exception e) when (IsNative(e))
            {
                throw Failed($"set up its {codec.Name} encoder for a {_width}×{_height} video", e);
            }

            if (_sound is not null)
            {
                AddSoundStream(_sound);
            }

            Native("start writing the MP4 file", ((delegate* unmanaged[Stdcall]<IntPtr, int>)MediaFoundation.Method(_writer, 5))(_writer));
        }
        finally
        {
            MediaFoundation.Release(attributes);
            MediaFoundation.Release(output);
            MediaFoundation.Release(input);
        }
    }

    void AddSoundStream(SoundFormat sound)
    {
        IntPtr output = 0, input = 0;
        try
        {
            MediaFoundation.Check(MediaFoundation.MFCreateMediaType(&output));
            DescribeSound(output, MediaFoundation.AacSubtype, sound);
            MediaFoundation.SetUInt32(output, AudioBytesPerSecond, AacBytesPerSecond);
            uint stream;
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)MediaFoundation.Method(_writer, 3))(_writer, output, &stream));
            _soundStream = stream;

            MediaFoundation.Check(MediaFoundation.MFCreateMediaType(&input));
            DescribeSound(input, MediaFoundation.PcmSubtype, sound);
            MediaFoundation.SetUInt32(input, AudioBlockAlignment, (uint)sound.BlockAlign);
            MediaFoundation.SetUInt32(input, AudioBytesPerSecond, (uint)sound.BytesPerSecond);
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)MediaFoundation.Method(_writer, 4))(_writer, _soundStream, input, 0));
        }
        catch (Exception e) when (IsNative(e))
        {
            throw Failed($"set up its AAC encoder for {sound.SampleRate / 1000.0:0.#} kHz sound", e);
        }
        finally
        {
            MediaFoundation.Release(output);
            MediaFoundation.Release(input);
        }
    }

    static void DescribeSound(IntPtr type, Guid subtype, SoundFormat sound)
    {
        MediaFoundation.SetGuid(type, MajorType, MediaFoundation.MajorAudio);
        MediaFoundation.SetGuid(type, Subtype, subtype);
        MediaFoundation.SetUInt32(type, AudioBitsPerSample, 16);
        MediaFoundation.SetUInt32(type, AudioSamplesPerSecond, (uint)sound.SampleRate);
        MediaFoundation.SetUInt32(type, AudioChannels, (uint)sound.Channels);
    }

    void Describe(IntPtr type, Guid subtype)
    {
        MediaFoundation.SetGuid(type, MajorType, MediaFoundation.MajorVideo);
        MediaFoundation.SetGuid(type, Subtype, subtype);
        MediaFoundation.SetUInt32(type, InterlaceMode, Progressive);
        MediaFoundation.SetUInt64(type, FrameSize, (ulong)_width << 32 | (uint)_height);
        MediaFoundation.SetUInt64(type, FrameRate, (ulong)_framesPerSecond << 32 | 1);
        MediaFoundation.SetUInt64(type, PixelAspectRatio, 1UL << 32 | 1);
        MediaFoundation.SetUInt32(type, YuvMatrix, Bt709Matrix);
        MediaFoundation.SetUInt32(type, NominalRange, Range16To235);
        MediaFoundation.SetUInt32(type, Primaries, Bt709Primaries);
        MediaFoundation.SetUInt32(type, TransferFunction, Bt709Transfer);
    }

    /// <summary>Adds the next picture, packed RGB at the writer's size. An unchanged one isn't converted again.</summary>
    public void Write(ReadOnlySpan<byte> rgb, bool changed = true)
    {
        ObjectDisposedException.ThrowIf(_writer == 0 || _finished, this);
        if (changed || _frames == 0)
        {
            Nv12.FromRgb(rgb, _width, _height, _nv12);
        }

        try
        {
            WriteSample(_stream, _nv12, _frames * TimeSpan.TicksPerSecond / _framesPerSecond, TimeSpan.TicksPerSecond / _framesPerSecond);
        }
        catch (Exception e) when (IsNative(e))
        {
            throw Failed("encode the video", e);
        }

        _frames++;
    }

    /// <summary>Adds sound after the picture it goes with: 16-bit PCM in the writer's <see cref="SoundFormat"/>.</summary>
    public void WriteSound(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_writer == 0 || _finished, this);
        if (_sound is null || pcm.Length < _sound.BlockAlign)
        {
            return;
        }

        long frames = pcm.Length / _sound.BlockAlign;
        try
        {
            WriteSample(_soundStream, pcm[..(int)(frames * _sound.BlockAlign)], _soundFrames * TimeSpan.TicksPerSecond / _sound.SampleRate,
                frames * TimeSpan.TicksPerSecond / _sound.SampleRate);
        }
        catch (Exception e) when (IsNative(e))
        {
            throw Failed("encode the sound", e);
        }

        _soundFrames += frames;
    }

    void WriteSample(uint stream, ReadOnlySpan<byte> bytes, long time, long duration)
    {
        IntPtr buffer = 0, sample = 0;
        try
        {
            MediaFoundation.Check(MediaFoundation.MFCreateMemoryBuffer((uint)bytes.Length, &buffer));
            byte* data;
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)MediaFoundation.Method(buffer, 3))(buffer, &data, null, null));
            bytes.CopyTo(new Span<byte>(data, bytes.Length));
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)MediaFoundation.Method(buffer, 4))(buffer));
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)MediaFoundation.Method(buffer, 6))(buffer, (uint)bytes.Length));

            // IMFSample: SetSampleTime 36, SetSampleDuration 38, AddBuffer 42; times in 100 ns units.
            MediaFoundation.Check(MediaFoundation.MFCreateSample(&sample));
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)MediaFoundation.Method(sample, 42))(sample, buffer));
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)MediaFoundation.Method(sample, 36))(sample, time));
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)MediaFoundation.Method(sample, 38))(sample, duration));

            // IMFSinkWriter.WriteSample, slot 6.
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)MediaFoundation.Method(_writer, 6))(_writer, stream, sample));
        }
        finally
        {
            MediaFoundation.Release(sample);
            MediaFoundation.Release(buffer);
        }
    }

    /// <summary>Writes out what the encoder still holds and closes the file. Without it the file isn't a playable MP4.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_writer == 0 || _finished, this);
        _finished = true;
        if (_frames > 0)
        {
            // IMFSinkWriter.Finalize, slot 11.
            Native("finish the MP4 file", ((delegate* unmanaged[Stdcall]<IntPtr, int>)MediaFoundation.Method(_writer, 11))(_writer));
        }
    }

    public void Dispose()
    {
        MediaFoundation.Release(_writer);
        _writer = 0;
        if (_started)
        {
            MediaFoundation.MFShutdown();
            _started = false;
        }

        if (_comInitialized)
        {
            MediaFoundation.CoUninitialize();
            _comInitialized = false;
        }
    }

    static void Native(string what, int hr)
    {
        if (hr < 0)
        {
            throw Failed(what, Marshal.GetExceptionForHR(hr)!);
        }
    }

    static bool IsNative(Exception e) => e is COMException or DllNotFoundException or EntryPointNotFoundException;

    static VideoEncoderException Failed(string what, Exception e) =>
        new($"Windows couldn't {what}{(e is DllNotFoundException ? ": this Windows has no Media Foundation" : $" (0x{e.HResult:X8})")}.", e);
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Orrery.Streaming;

namespace Orrery.Protocols.Stream.Windows;

/// <summary>
/// A decoded frame on the GPU: slice <paramref name="Subresource"/> of an NV12 texture array (an ID3D11Texture2D*),
/// <paramref name="Width"/> x <paramref name="Height"/> of it being picture. Owned by the decoder and valid until its
/// next <see cref="MediaFoundationDecoder.Decode"/>.
/// </summary>
sealed record DecodedFrame(nint Texture, int Subresource, int Width, int Height);

/// <summary>
/// Decodes H.264, HEVC or AV1 with the Media Foundation decoder Windows has for it, on the GPU through a D3D11 device
/// (DXVA), in low-latency mode so each frame comes out as soon as it goes in. Windows ships the H.264 decoder; HEVC
/// and AV1 come from the Microsoft Store's video extensions. No codec ships with Orrery.
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe class MediaFoundationDecoder : IDisposable
{
    static readonly Lazy<bool> Started = new(() =>
    {
        Com.Check("MFStartup", MFStartup(Win.MFVersion, Win.MFStartupFull));
        return true;
    });

    nint _transform;
    nint _deviceManager;
    nint _outputSample;
    nint _outputTexture;
    int _width;
    int _height;

    MediaFoundationDecoder(nint transform, nint deviceManager, string name)
    {
        _transform = transform;
        _deviceManager = deviceManager;
        Name = name;
    }

    /// <summary>Which decoder Windows picked, as it names itself.</summary>
    public string Name { get; }

    /// <summary>
    /// Opens the decoder for <paramref name="codec"/> on <paramref name="device"/>, an ID3D11Device* created with video
    /// support and made multithread-protected. Throws <see cref="NotSupportedException"/> when Windows has no decoder
    /// for it, or none that decodes on the GPU.
    /// </summary>
    public static MediaFoundationDecoder Create(nint device, VideoCodec codec)
    {
        _ = Started.Value;
        var subtype = Subtype(codec);
        nint transform = Activate(subtype, codec, out string name);
        nint attributes = 0, deviceManager = 0, inputType = 0;
        MediaFoundationDecoder? decoder = null;
        try
        {
            Com.Check("IMFTransform::GetAttributes", ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(transform, Slot.TransformGetAttributes))(transform, &attributes));
            if (!MFAttributes.TryGetUInt32(attributes, MFGuid.D3D11Aware, out uint aware) || aware == 0)
            {
                throw new NotSupportedException($"{name} can't decode on the GPU.");
            }

            // Without this the decoder holds frames back to reorder them, which OSP's streams never need.
            MFAttributes.SetUInt32(attributes, MFGuid.LowLatency, 1);

            uint token;
            Com.Check("MFCreateDXGIDeviceManager", MFCreateDXGIDeviceManager(&token, &deviceManager));
            Com.Check("IMFDXGIDeviceManager::ResetDevice", ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Com.Slot(deviceManager, Slot.DXGIDeviceManagerResetDevice))(deviceManager, device, token));
            Message(transform, Win.MftMessageSetD3DManager, deviceManager);

            Com.Check("MFCreateMediaType", MFCreateMediaType(&inputType));
            MFAttributes.SetGuid(inputType, MFGuid.MajorType, MFGuid.MediaTypeVideo);
            MFAttributes.SetGuid(inputType, MFGuid.Subtype, subtype);
            MFAttributes.SetUInt32(inputType, MFGuid.InterlaceMode, (uint)Win.VideoInterlaceProgressive);
            // Some decoders (AV1's) offer no output type until the input has a size. The real size comes from the first
            // keyframe, as a stream change, so any will do here.
            MFAttributes.SetUInt64(inputType, MFGuid.FrameSize, (1920UL << 32) | 1080);
            Com.Check("IMFTransform::SetInputType", ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Com.Slot(transform, Slot.TransformSetInputType))(transform, 0, inputType, 0));

            decoder = new MediaFoundationDecoder(transform, deviceManager, name);
            transform = 0;
            deviceManager = 0;
            decoder.ChooseOutputType();
            MftOutputStreamInfo info;
            Com.Check("IMFTransform::GetOutputStreamInfo", ((delegate* unmanaged[Stdcall]<nint, uint, MftOutputStreamInfo*, int>)Com.Slot(decoder._transform, Slot.TransformGetOutputStreamInfo))(decoder._transform, 0, &info));
            if ((info.Flags & (Win.MftOutputStreamProvidesSamples | Win.MftOutputStreamCanProvideSamples)) == 0)
            {
                throw new NotSupportedException($"{name} wants its output buffers supplied, which this decoder doesn't do.");
            }

            Message(decoder._transform, Win.MftMessageNotifyBeginStreaming, 0);
            Message(decoder._transform, Win.MftMessageNotifyStartOfStream, 0);
            var opened = decoder;
            decoder = null;
            return opened;
        }
        finally
        {
            decoder?.Dispose();
            Com.Release(inputType);
            Com.Release(attributes);
            Com.Release(deviceManager);
            Com.Release(transform);
        }
    }

    /// <summary>
    /// Decodes one encoded frame. True with <paramref name="frame"/> when a picture came out, which with low latency
    /// is every frame once the stream has started on a keyframe; false while the decoder still needs more.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> encoded, long timestamp100ns, out DecodedFrame? frame)
    {
        frame = null;
        nint buffer = 0, sample = 0;
        try
        {
            Com.Check("MFCreateMemoryBuffer", MFCreateMemoryBuffer((uint)encoded.Length, &buffer));
            byte* data;
            uint max, current;
            Com.Check("IMFMediaBuffer::Lock", ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Com.Slot(buffer, Slot.MediaBufferLock))(buffer, &data, &max, &current));
            encoded.CopyTo(new Span<byte>(data, encoded.Length));
            ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(buffer, Slot.MediaBufferUnlock))(buffer);
            Com.Check("IMFMediaBuffer::SetCurrentLength", ((delegate* unmanaged[Stdcall]<nint, uint, int>)Com.Slot(buffer, Slot.MediaBufferSetCurrentLength))(buffer, (uint)encoded.Length));
            Com.Check("MFCreateSample", MFCreateSample(&sample));
            Com.Check("IMFSample::AddBuffer", ((delegate* unmanaged[Stdcall]<nint, nint, int>)Com.Slot(sample, Slot.SampleAddBuffer))(sample, buffer));
            Com.Check("IMFSample::SetSampleTime", ((delegate* unmanaged[Stdcall]<nint, long, int>)Com.Slot(sample, Slot.SampleSetSampleTime))(sample, timestamp100ns));
            int input = ProcessInput(sample);
            if (input == Win.ENotAccepting)
            {
                // Output is waiting: take it, then the input goes in.
                Drain();
                input = ProcessInput(sample);
            }

            Com.Check("IMFTransform::ProcessInput", input);
        }
        finally
        {
            Com.Release(sample);
            Com.Release(buffer);
        }

        if (!Drain())
        {
            return false;
        }

        uint subresource;
        nint dxgiBuffer = 0, mediaBuffer = 0;
        try
        {
            Com.Check("IMFSample::GetBufferByIndex", ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Com.Slot(_outputSample, Slot.SampleGetBufferByIndex))(_outputSample, 0, &mediaBuffer));
            dxgiBuffer = Com.QueryInterface(mediaBuffer, Iid.MFDXGIBuffer);
            nint texture;
            var iid = Iid.Texture2D;
            Com.Check("IMFDXGIBuffer::GetResource", ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Slot(dxgiBuffer, Slot.DXGIBufferGetResource))(dxgiBuffer, &iid, &texture));
            Com.Release(ref _outputTexture);
            _outputTexture = texture;
            Com.Check("IMFDXGIBuffer::GetSubresourceIndex", ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Slot(dxgiBuffer, Slot.DXGIBufferGetSubresourceIndex))(dxgiBuffer, &subresource));
        }
        finally
        {
            Com.Release(dxgiBuffer);
            Com.Release(mediaBuffer);
        }

        frame = new DecodedFrame(_outputTexture, (int)subresource, _width, _height);
        return true;
    }

    int ProcessInput(nint sample) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Com.Slot(_transform, Slot.TransformProcessInput))(_transform, 0, sample, 0);

    /// <summary>
    /// Takes every picture the decoder has ready, keeping only the newest in <see cref="_outputSample"/>; true if there
    /// was at least one. Follows a change of stream format (the first keyframe gives the real size) on the way.
    /// </summary>
    bool Drain()
    {
        bool any = false;
        while (true)
        {
            MftOutputDataBuffer output = default;
            uint status;
            int result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, MftOutputDataBuffer*, uint*, int>)Com.Slot(_transform, Slot.TransformProcessOutput))(_transform, 0, 1, &output, &status);
            Com.Release(output.Events);
            if (result == Win.ETransformNeedMoreInput)
            {
                return any;
            }

            if (result == Win.ETransformStreamChange)
            {
                Com.Release(output.Sample);
                ChooseOutputType();
                continue;
            }

            Com.Check("IMFTransform::ProcessOutput", result);
            if (output.Sample != 0)
            {
                Com.Release(ref _outputSample);
                _outputSample = output.Sample;
                any = true;
            }
        }
    }

    /// <summary>Picks NV12 from the output types the decoder offers, and reads the picture size from it.</summary>
    void ChooseOutputType()
    {
        for (uint i = 0; ; i++)
        {
            nint type = 0;
            int result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Com.Slot(_transform, Slot.TransformGetOutputAvailableType))(_transform, 0, i, &type);
            if (result == Win.ENoMoreTypes)
            {
                throw new NotSupportedException($"{Name} doesn't output NV12.");
            }

            Com.Check("IMFTransform::GetOutputAvailableType", result);
            try
            {
                if (!MFAttributes.TryGetGuid(type, MFGuid.Subtype, out var subtype) || subtype != MFGuid.VideoFormatNv12)
                {
                    continue;
                }

                Com.Check("IMFTransform::SetOutputType", ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Com.Slot(_transform, Slot.TransformSetOutputType))(_transform, 0, type, 0));
                if (MFAttributes.TryGetUInt64(type, MFGuid.FrameSize, out ulong size))
                {
                    _width = (int)(size >> 32);
                    _height = (int)(size & 0xFFFFFFFF);
                }

                return;
            }
            finally
            {
                Com.Release(type);
            }
        }
    }

    static nint Activate(Guid subtype, VideoCodec codec, out string name)
    {
        var input = new MftRegisterTypeInfo { MajorType = MFGuid.MediaTypeVideo, Subtype = subtype };
        nint* activates = null;
        uint count = 0;
        Com.Check("MFTEnumEx", MFTEnumEx(MFGuid.CategoryVideoDecoder, (uint)(Win.MftEnumSyncMft | Win.MftEnumLocalMft | Win.MftEnumSortAndFilter), &input, null, &activates, &count));
        try
        {
            if (count == 0)
            {
                throw new NotSupportedException(codec switch
                {
                    VideoCodec.Hevc => "Windows has no HEVC decoder; the HEVC Video Extensions from the Microsoft Store add one.",
                    VideoCodec.Av1 => "Windows has no AV1 decoder; the AV1 Video Extension from the Microsoft Store adds one.",
                    _ => "Windows has no H.264 decoder (an N edition without the Media Feature Pack?).",
                });
            }

            nint transform;
            var iid = Iid.MFTransform;
            Com.Check("IMFActivate::ActivateObject", ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Slot(activates[0], Slot.ActivateActivateObject))(activates[0], &iid, &transform));
            name = MFAttributes.TryGetString(activates[0], MFGuid.FriendlyName) ?? codec.ToString();
            return transform;
        }
        finally
        {
            for (uint i = 0; i < count; i++)
            {
                Com.Release(activates[i]);
            }

            Marshal.FreeCoTaskMem((nint)activates);
        }
    }

    static Guid Subtype(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => MFGuid.VideoFormatH264,
        VideoCodec.Hevc => MFGuid.VideoFormatHevc,
        _ => MFGuid.VideoFormatAv1,
    };

    static void Message(nint transform, int message, nint parameter) =>
        Com.Check($"IMFTransform::ProcessMessage({message})", ((delegate* unmanaged[Stdcall]<nint, int, nint, int>)Com.Slot(transform, Slot.TransformProcessMessage))(transform, message, parameter));

    public void Dispose()
    {
        Com.Release(ref _outputTexture);
        Com.Release(ref _outputSample);
        if (_transform != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, int, nint, int>)Com.Slot(_transform, Slot.TransformProcessMessage))(_transform, Win.MftMessageSetD3DManager, 0);
        }

        Com.Release(ref _transform);
        Com.Release(ref _deviceManager);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    static extern int MFStartup(uint version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    static extern int MFTEnumEx(Guid category, uint flags, MftRegisterTypeInfo* input, MftRegisterTypeInfo* output, nint** activates, uint* count);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    static extern int MFCreateMediaType(nint* type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    static extern int MFCreateMemoryBuffer(uint length, nint* buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    static extern int MFCreateSample(nint* sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    static extern int MFCreateDXGIDeviceManager(uint* token, nint* manager);
}

/// <summary>IMFAttributes calls (media types, MFTs and activation objects all are attribute stores). Keys are copied to the stack first.</summary>
[SupportedOSPlatform("windows")]
static unsafe class MFAttributes
{
    public static bool TryGetUInt32(nint attributes, Guid key, out uint value)
    {
        uint result;
        bool ok = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Com.Slot(attributes, Slot.AttributesGetUINT32))(attributes, &key, &result) >= 0;
        value = result;
        return ok;
    }

    public static bool TryGetUInt64(nint attributes, Guid key, out ulong value)
    {
        ulong result;
        bool ok = ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Com.Slot(attributes, Slot.AttributesGetUINT64))(attributes, &key, &result) >= 0;
        value = result;
        return ok;
    }

    public static bool TryGetGuid(nint attributes, Guid key, out Guid value)
    {
        Guid result;
        bool ok = ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.Slot(attributes, Slot.AttributesGetGUID))(attributes, &key, &result) >= 0;
        value = result;
        return ok;
    }

    public static string? TryGetString(nint attributes, Guid key)
    {
        char* value = null;
        uint length;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, char**, uint*, int>)Com.Slot(attributes, Slot.AttributesGetAllocatedString))(attributes, &key, &value, &length) < 0)
        {
            return null;
        }

        try
        {
            return new string(value, 0, (int)length);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)value);
        }
    }

    public static void SetUInt32(nint attributes, Guid key, uint value) =>
        Com.Check("IMFAttributes::SetUINT32", ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Com.Slot(attributes, Slot.AttributesSetUINT32))(attributes, &key, value));

    public static void SetUInt64(nint attributes, Guid key, ulong value) =>
        Com.Check("IMFAttributes::SetUINT64", ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Com.Slot(attributes, Slot.AttributesSetUINT64))(attributes, &key, value));

    public static void SetGuid(nint attributes, Guid key, Guid value) =>
        Com.Check("IMFAttributes::SetGUID", ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.Slot(attributes, Slot.AttributesSetGUID))(attributes, &key, &value));
}

namespace Orrery.Host.Encoding.Nvenc;

/// <summary>What an NVENC encoder can do with one codec, from its caps.</summary>
sealed record NvEncCodecCaps(
    Guid Codec,
    int MaxWidth,
    int MaxHeight,
    bool TenBit,
    bool Yuv444,
    bool IntraRefresh,
    bool RefPicInvalidation,
    int Engines);

/// <summary>
/// An NVENC encode session on a D3D11 device. So far it opens and answers what the GPU can encode; initializing an
/// encoder and encoding frames come with capture in the phase 1 spike (docs/design-stream.md §11).
/// </summary>
sealed unsafe class NvEncSession : IDisposable
{
    readonly NvEncApi _api;
    nint _encoder;

    NvEncSession(NvEncApi api, nint encoder)
    {
        _api = api;
        _encoder = encoder;
    }

    /// <summary>Opens a session on <paramref name="d3d11Device"/> (an ID3D11Device*), which must be on an NVIDIA adapter.</summary>
    public static NvEncSession Open(NvEncApi api, nint d3d11Device)
    {
        var parameters = new NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
        {
            version = NvEnc.OpenEncodeSessionExParamsVer,
            deviceType = NvEnc.DeviceTypeDirectX,
            device = d3d11Device,
            apiVersion = NvEnc.ApiVersion,
        };
        nint encoder = 0;
        var status = api.OpenEncodeSessionEx(&parameters, &encoder);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            // The driver may hand back a half-open session even on failure; it still has to be destroyed.
            string? detail = encoder != 0 ? api.GetLastErrorString(encoder) : null;
            if (encoder != 0)
            {
                api.DestroyEncoder(encoder);
            }

            throw new NvEncException("NvEncOpenEncodeSessionEx", status, detail);
        }

        return new NvEncSession(api, encoder);
    }

    /// <summary>The codecs this GPU encodes, as NVENC codec GUIDs (<see cref="NvEnc.CodecH264"/> and the rest).</summary>
    public IReadOnlyList<Guid> Codecs()
    {
        uint count = 0;
        Check("NvEncGetEncodeGUIDCount", _api.GetEncodeGuidCount(_encoder, &count));
        var guids = new Guid[count];
        uint written = 0;
        fixed (Guid* buffer = guids)
        {
            Check("NvEncGetEncodeGUIDs", _api.GetEncodeGuids(_encoder, buffer, count, &written));
        }

        return guids[..(int)Math.Min(written, count)];
    }

    public int Cap(Guid codec, NV_ENC_CAPS cap)
    {
        var parameters = new NV_ENC_CAPS_PARAM { version = NvEnc.CapsParamVer, capsToQuery = cap };
        int value = 0;
        Check("NvEncGetEncodeCaps", _api.GetEncodeCaps(_encoder, codec, &parameters, &value));
        return value;
    }

    public NvEncCodecCaps Caps(Guid codec) => new(
        codec,
        Cap(codec, NV_ENC_CAPS.NV_ENC_CAPS_WIDTH_MAX),
        Cap(codec, NV_ENC_CAPS.NV_ENC_CAPS_HEIGHT_MAX),
        Cap(codec, NV_ENC_CAPS.NV_ENC_CAPS_SUPPORT_10BIT_ENCODE) != 0,
        Cap(codec, NV_ENC_CAPS.NV_ENC_CAPS_SUPPORT_YUV444_ENCODE) != 0,
        Cap(codec, NV_ENC_CAPS.NV_ENC_CAPS_SUPPORT_INTRA_REFRESH) != 0,
        Cap(codec, NV_ENC_CAPS.NV_ENC_CAPS_SUPPORT_REF_PIC_INVALIDATION) != 0,
        Cap(codec, NV_ENC_CAPS.NV_ENC_CAPS_NUM_ENCODER_ENGINES));

    public static string CodecName(Guid codec) =>
        codec == NvEnc.CodecH264 ? "H.264" : codec == NvEnc.CodecHevc ? "HEVC" : codec == NvEnc.CodecAv1 ? "AV1" : codec.ToString();

    void Check(string call, NVENCSTATUS status)
    {
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            throw new NvEncException(call, status, _api.GetLastErrorString(_encoder));
        }
    }

    public void Dispose()
    {
        if (_encoder != 0)
        {
            _api.DestroyEncoder(_encoder);
            _encoder = 0;
        }
    }
}

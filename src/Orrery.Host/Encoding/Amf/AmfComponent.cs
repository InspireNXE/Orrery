using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Host.Encoding.Amf;

/// <summary>What an AMF encoder component says it can do.</summary>
sealed record AmfEncoderCaps(bool Hardware, int MaxWidth, int MaxHeight, long MaxBitrate, long HardwareInstances);

/// <summary>
/// An AMF component, such as an encoder: properties, then Init, then frames in through <see cref="SubmitInput"/> and
/// bitstream out through <see cref="QueryOutput"/>.
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe class AmfComponent : IDisposable
{
    nint _component;

    internal AmfComponent(nint component) => _component = component;

    public void SetInt64(string name, long value) => Set(name, new AMFVariantStruct { type = AMF_VARIANT_TYPE.AMF_VARIANT_INT64, int64Value = value });

    public void SetSize(string name, int width, int height) =>
        Set(name, new AMFVariantStruct { type = AMF_VARIANT_TYPE.AMF_VARIANT_SIZE, sizeValue = new AMFSize { width = width, height = height } });

    public void SetRate(string name, uint numerator, uint denominator) =>
        Set(name, new AMFVariantStruct { type = AMF_VARIANT_TYPE.AMF_VARIANT_RATE, rateValue = new AMFRate { num = numerator, den = denominator } });

    void Set(string name, AMFVariantStruct value)
    {
        fixed (char* key = name)
        {
            AmfCom.Check($"AMFComponent::SetProperty({name})", ((delegate* unmanaged[Stdcall]<nint, char*, AMFVariantStruct, AMF_RESULT>)AmfCom.Slot(_component, AmfSlot.ComponentSetProperty))(_component, key, value));
        }
    }

    /// <summary>The encoder's caps; <paramref name="maxBitrate"/> and <paramref name="instances"/> are its codec's cap property names.</summary>
    public AmfEncoderCaps Caps(string maxBitrate, string instances)
    {
        nint caps = 0;
        AmfCom.Check("AMFComponent::GetCaps", ((delegate* unmanaged[Stdcall]<nint, nint*, AMF_RESULT>)AmfCom.Slot(_component, AmfSlot.ComponentGetCaps))(_component, &caps));
        nint input = 0;
        try
        {
            int acceleration = ((delegate* unmanaged[Stdcall]<nint, int>)AmfCom.Slot(caps, AmfSlot.CapsGetAccelerationType))(caps);
            AmfCom.Check("AMFCaps::GetInputCaps", ((delegate* unmanaged[Stdcall]<nint, nint*, AMF_RESULT>)AmfCom.Slot(caps, AmfSlot.CapsGetInputCaps))(caps, &input));
            int minWidth, maxWidth, minHeight, maxHeight;
            ((delegate* unmanaged[Stdcall]<nint, int*, int*, void>)AmfCom.Slot(input, AmfSlot.IOCapsGetWidthRange))(input, &minWidth, &maxWidth);
            ((delegate* unmanaged[Stdcall]<nint, int*, int*, void>)AmfCom.Slot(input, AmfSlot.IOCapsGetHeightRange))(input, &minHeight, &maxHeight);
            return new AmfEncoderCaps(acceleration == Amf.AccelHardware, maxWidth, maxHeight, CapInt64(caps, maxBitrate), CapInt64(caps, instances));
        }
        finally
        {
            AmfCom.Release(input);
            AmfCom.Release(caps);
        }
    }

    static long CapInt64(nint caps, string name)
    {
        AMFVariantStruct value;
        fixed (char* key = name)
        {
            var result = ((delegate* unmanaged[Stdcall]<nint, char*, AMFVariantStruct*, AMF_RESULT>)AmfCom.Slot(caps, AmfSlot.CapsGetProperty))(caps, key, &value);
            return result == AMF_RESULT.AMF_OK && value.type == AMF_VARIANT_TYPE.AMF_VARIANT_INT64 ? value.int64Value : 0;
        }
    }

    public void Init(int format, int width, int height) =>
        AmfCom.Check("AMFComponent::Init", ((delegate* unmanaged[Stdcall]<nint, int, int, int, AMF_RESULT>)AmfCom.Slot(_component, AmfSlot.ComponentInit))(_component, format, width, height));

    /// <summary>Hands the encoder a surface (an AMFSurface*, which is an AMFData*); AMF_INPUT_FULL means collect output first.</summary>
    public AMF_RESULT SubmitInput(nint data) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, AMF_RESULT>)AmfCom.Slot(_component, AmfSlot.ComponentSubmitInput))(_component, data);

    /// <summary>The next output, or AMF_REPEAT when there isn't one yet. The caller releases <paramref name="data"/>.</summary>
    public AMF_RESULT QueryOutput(out nint data)
    {
        nint output = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, nint*, AMF_RESULT>)AmfCom.Slot(_component, AmfSlot.ComponentQueryOutput))(_component, &output);
        data = output;
        return result;
    }

    /// <summary>Copies an encoder output (an AMFData* holding an AMFBuffer) out as bytes.</summary>
    public static byte[] ReadBuffer(nint data)
    {
        nint buffer = AmfCom.QueryInterface(data, Amf.IidBuffer);
        try
        {
            nuint size = ((delegate* unmanaged[Stdcall]<nint, nuint>)AmfCom.Slot(buffer, AmfSlot.BufferGetSize))(buffer);
            void* native = ((delegate* unmanaged[Stdcall]<nint, void*>)AmfCom.Slot(buffer, AmfSlot.BufferGetNative))(buffer);
            return new ReadOnlySpan<byte>(native, checked((int)size)).ToArray();
        }
        finally
        {
            AmfCom.Release(buffer);
        }
    }

    public void Dispose()
    {
        if (_component != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, AMF_RESULT>)AmfCom.Slot(_component, AmfSlot.ComponentTerminate))(_component);
            AmfCom.Release(_component);
            _component = 0;
        }
    }
}

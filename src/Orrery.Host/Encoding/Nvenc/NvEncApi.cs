using System.Runtime.InteropServices;

namespace Orrery.Host.Encoding.Nvenc;

/// <summary>An NVENC call failed: <see cref="Status"/>, with the driver's own explanation when it gave one.</summary>
sealed class NvEncException(string call, NVENCSTATUS status, string? detail = null)
    : Exception(detail is { Length: > 0 } ? $"{call} failed: {status} ({detail})" : $"{call} failed: {status}")
{
    public NVENCSTATUS Status { get; } = status;
}

/// <summary>
/// NVIDIA's encoder API, from the driver's own library (nvEncodeAPI64.dll, libnvidia-encode.so.1): nothing of NVIDIA's
/// ships with Orrery Host. Built against NVENC API 12.1 (<see cref="NvEnc.ApiVersion"/>); the structs and constants
/// are generated from the header (NvEncInterop.g.cs). Each member here is one driver call with no policy of its own;
/// <see cref="NvEncSession"/> builds on them.
/// </summary>
sealed unsafe class NvEncApi
{
    /// <summary>The oldest driver API this build works with, as NvEncodeAPIGetMaxSupportedVersion reports it (major &lt;&lt; 4 | minor).</summary>
    public const uint RequiredVersion = (12 << 4) | 1;

    readonly NV_ENCODE_API_FUNCTION_LIST _functions;

    NvEncApi(NV_ENCODE_API_FUNCTION_LIST functions, uint driverVersion)
    {
        _functions = functions;
        DriverVersion = driverVersion;
    }

    /// <summary>The newest API the driver supports, major &lt;&lt; 4 | minor.</summary>
    public uint DriverVersion { get; }

    public static string FormatVersion(uint version) => $"{version >> 4}.{version & 0xF}";

    /// <summary>
    /// Loads the driver's NVENC library, or returns null with a sentence saying why not. The library stays loaded for the
    /// life of the process, as the driver expects.
    /// </summary>
    public static NvEncApi? TryLoad(out string? reason)
    {
        string name = OperatingSystem.IsWindows() ? "nvEncodeAPI64.dll" : "libnvidia-encode.so.1";
        if (!NativeLibrary.TryLoad(name, out var library))
        {
            reason = "no NVIDIA driver with NVENC is installed";
            return null;
        }

        if (!NativeLibrary.TryGetExport(library, "NvEncodeAPIGetMaxSupportedVersion", out var getMaxVersion)
            || !NativeLibrary.TryGetExport(library, "NvEncodeAPICreateInstance", out var createInstance))
        {
            reason = $"{name} doesn't export the NVENC entry points";
            return null;
        }

        uint driverVersion;
        var status = ((delegate* unmanaged[Stdcall]<uint*, NVENCSTATUS>)getMaxVersion)(&driverVersion);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            reason = $"NvEncodeAPIGetMaxSupportedVersion failed: {status}";
            return null;
        }

        if (driverVersion < RequiredVersion)
        {
            reason = $"the driver supports NVENC {FormatVersion(driverVersion)}; Orrery Host needs {FormatVersion(RequiredVersion)} (driver 531.61 or later)";
            return null;
        }

        var functions = new NV_ENCODE_API_FUNCTION_LIST { version = NvEnc.FunctionListVer };
        status = ((delegate* unmanaged[Stdcall]<NV_ENCODE_API_FUNCTION_LIST*, NVENCSTATUS>)createInstance)(&functions);
        if (status != NVENCSTATUS.NV_ENC_SUCCESS)
        {
            reason = $"NvEncodeAPICreateInstance failed: {status}";
            return null;
        }

        reason = null;
        return new NvEncApi(functions, driverVersion);
    }

    public NVENCSTATUS OpenEncodeSessionEx(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS* parameters, nint* encoder) =>
        ((delegate* unmanaged[Stdcall]<NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS*, nint*, NVENCSTATUS>)_functions.nvEncOpenEncodeSessionEx)(parameters, encoder);

    public NVENCSTATUS DestroyEncoder(nint encoder) =>
        ((delegate* unmanaged[Stdcall]<nint, NVENCSTATUS>)_functions.nvEncDestroyEncoder)(encoder);

    public NVENCSTATUS GetEncodeGuidCount(nint encoder, uint* count) =>
        ((delegate* unmanaged[Stdcall]<nint, uint*, NVENCSTATUS>)_functions.nvEncGetEncodeGUIDCount)(encoder, count);

    public NVENCSTATUS GetEncodeGuids(nint encoder, Guid* guids, uint arraySize, uint* count) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, uint*, NVENCSTATUS>)_functions.nvEncGetEncodeGUIDs)(encoder, guids, arraySize, count);

    public NVENCSTATUS GetEncodeCaps(nint encoder, Guid codec, NV_ENC_CAPS_PARAM* parameters, int* value) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid, NV_ENC_CAPS_PARAM*, int*, NVENCSTATUS>)_functions.nvEncGetEncodeCaps)(encoder, codec, parameters, value);

    public NVENCSTATUS GetEncodePresetConfigEx(nint encoder, Guid codec, Guid preset, uint tuning, NV_ENC_PRESET_CONFIG* config) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid, Guid, uint, NV_ENC_PRESET_CONFIG*, NVENCSTATUS>)_functions.nvEncGetEncodePresetConfigEx)(encoder, codec, preset, tuning, config);

    public NVENCSTATUS InitializeEncoder(nint encoder, NV_ENC_INITIALIZE_PARAMS* parameters) =>
        ((delegate* unmanaged[Stdcall]<nint, NV_ENC_INITIALIZE_PARAMS*, NVENCSTATUS>)_functions.nvEncInitializeEncoder)(encoder, parameters);

    public NVENCSTATUS CreateBitstreamBuffer(nint encoder, NV_ENC_CREATE_BITSTREAM_BUFFER* parameters) =>
        ((delegate* unmanaged[Stdcall]<nint, NV_ENC_CREATE_BITSTREAM_BUFFER*, NVENCSTATUS>)_functions.nvEncCreateBitstreamBuffer)(encoder, parameters);

    public NVENCSTATUS DestroyBitstreamBuffer(nint encoder, nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, NVENCSTATUS>)_functions.nvEncDestroyBitstreamBuffer)(encoder, buffer);

    public NVENCSTATUS RegisterResource(nint encoder, NV_ENC_REGISTER_RESOURCE* parameters) =>
        ((delegate* unmanaged[Stdcall]<nint, NV_ENC_REGISTER_RESOURCE*, NVENCSTATUS>)_functions.nvEncRegisterResource)(encoder, parameters);

    public NVENCSTATUS UnregisterResource(nint encoder, nint registered) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, NVENCSTATUS>)_functions.nvEncUnregisterResource)(encoder, registered);

    public NVENCSTATUS MapInputResource(nint encoder, NV_ENC_MAP_INPUT_RESOURCE* parameters) =>
        ((delegate* unmanaged[Stdcall]<nint, NV_ENC_MAP_INPUT_RESOURCE*, NVENCSTATUS>)_functions.nvEncMapInputResource)(encoder, parameters);

    public NVENCSTATUS UnmapInputResource(nint encoder, nint mapped) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, NVENCSTATUS>)_functions.nvEncUnmapInputResource)(encoder, mapped);

    public NVENCSTATUS EncodePicture(nint encoder, NV_ENC_PIC_PARAMS* parameters) =>
        ((delegate* unmanaged[Stdcall]<nint, NV_ENC_PIC_PARAMS*, NVENCSTATUS>)_functions.nvEncEncodePicture)(encoder, parameters);

    public NVENCSTATUS LockBitstream(nint encoder, NV_ENC_LOCK_BITSTREAM* parameters) =>
        ((delegate* unmanaged[Stdcall]<nint, NV_ENC_LOCK_BITSTREAM*, NVENCSTATUS>)_functions.nvEncLockBitstream)(encoder, parameters);

    public NVENCSTATUS UnlockBitstream(nint encoder, nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, NVENCSTATUS>)_functions.nvEncUnlockBitstream)(encoder, buffer);

    /// <summary>Stops the encoder referring to the frame with this input timestamp: recovery from a lost frame without a keyframe.</summary>
    public NVENCSTATUS InvalidateRefFrames(nint encoder, ulong timestamp) =>
        ((delegate* unmanaged[Stdcall]<nint, ulong, NVENCSTATUS>)_functions.nvEncInvalidateRefFrames)(encoder, timestamp);

    /// <summary>The driver's description of the last failure on <paramref name="encoder"/>, if any.</summary>
    public string? GetLastErrorString(nint encoder) =>
        Marshal.PtrToStringUTF8((nint)((delegate* unmanaged[Stdcall]<nint, byte*>)_functions.nvEncGetLastErrorString)(encoder));
}

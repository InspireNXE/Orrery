using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Host.Platform.Windows;

/// <summary>A graphics adapter as DXGI lists it.</summary>
sealed record GpuAdapter(int Index, string Name, uint VendorId, ulong DedicatedVideoMemory, bool Software)
{
    public const uint Nvidia = 0x10DE;
    public const uint Amd = 0x1002;
    public const uint Intel = 0x8086;

    public string Vendor => VendorId switch
    {
        Nvidia => "NVIDIA",
        Amd => "AMD",
        Intel => "Intel",
        _ => $"vendor 0x{VendorId:X4}",
    };
}

/// <summary>
/// A monitor on the desktop: output <paramref name="OutputIndex"/> of adapter <paramref name="AdapterIndex"/> (DXGI's own
/// numbering, which <see cref="Dxgi.CreateDevice"/> takes), at <paramref name="Left"/>, <paramref name="Top"/> on the
/// virtual desktop.
/// </summary>
sealed record DisplayOutput(int AdapterIndex, int OutputIndex, uint VendorId, string AdapterName, string DeviceName, int Left, int Top, int Width, int Height);

/// <summary>
/// The adapters DXGI sees, and a D3D11 device on one of them: what capture and the encoders run on. Calls DXGI's COM
/// interfaces through their vtables, as WebFrames does with WebView2.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class Dxgi
{
    static readonly Guid IidFactory1 = new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

    static readonly Guid IidMultithread = new(0x9b7e4e00, 0x342c, 0x4106, 0xa1, 0x9f, 0x4f, 0x27, 0x04, 0xf6, 0x89, 0xf0);
    public static readonly Guid IidTexture2D = new(0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);

    // Slots checked against the Windows SDK's C vtable structs (offsetof / sizeof(void*)).
    const int QueryInterfaceSlot = 0;
    const int Release = 2;
    const int FactoryEnumAdapters1 = 12;
    const int AdapterEnumOutputs = 7;
    const int AdapterGetDesc1 = 10;
    const int OutputGetDesc = 7;
    const int DeviceCreateTexture2D = 5;
    const int DeviceGetImmediateContext = 40;
    const int ContextMap = 14;
    const int ContextUnmap = 15;
    const int ContextCopySubresourceRegion = 46;
    const int ContextCopyResource = 47;
    const int ContextUpdateSubresource = 48;
    const uint UsageStaging = 3;
    const uint CpuAccessRead = 0x20000;
    const int MapRead = 1;
    const uint FormatB8G8R8A8Unorm = 87;
    const int Texture2DGetDesc = 10;
    const int MultithreadSetProtected = 5;
    const int NotFound = unchecked((int)0x887A0002);
    const uint SoftwareAdapterFlag = 2;
    const int DriverTypeUnknown = 0;
    const uint CreateDeviceBgraSupport = 0x20;
    const uint SdkVersion = 7;

    /// <summary>DXGI_ADAPTER_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    /// <summary>DXGI_OUTPUT_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct OutputDesc
    {
        public fixed char DeviceName[32];
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }

    /// <summary>D3D11_TEXTURE2D_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    /// <summary>
    /// The adapters, each once. DXGI can list one GPU several times, each with its own LUID (seen under RDP with a
    /// virtual display attached), so later entries with the same vendor, device, subsystem and revision are left out
    /// and the first, which DXGI puts the primary display on, is kept. Two identical cards in one machine show as one
    /// until that matters.
    /// </summary>
    public static IReadOnlyList<GpuAdapter> Adapters()
    {
        var adapters = new List<GpuAdapter>();
        var seen = new HashSet<(uint, uint, uint, uint)>();
        nint factory = CreateFactory();
        try
        {
            for (int i = 0; TryEnumAdapter(factory, i, out var adapter); i++)
            {
                try
                {
                    AdapterDesc1 desc;
                    Check("IDXGIAdapter1::GetDesc1", ((delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)Slot(adapter, AdapterGetDesc1))(adapter, &desc));
                    if (!seen.Add((desc.VendorId, desc.DeviceId, desc.SubSysId, desc.Revision)))
                    {
                        continue;
                    }

                    string name = new string(desc.Description);
                    adapters.Add(new GpuAdapter(i, name, desc.VendorId, desc.DedicatedVideoMemory, (desc.Flags & SoftwareAdapterFlag) != 0));
                }
                finally
                {
                    ReleaseCom(adapter);
                }
            }
        }
        finally
        {
            ReleaseCom(factory);
        }

        return adapters;
    }

    /// <summary>
    /// The monitors attached to the desktop, in DXGI's order: adapter by adapter, the primary monitor first. Unlike
    /// <see cref="Adapters"/>, every adapter entry is searched, since a monitor belongs to one particular entry.
    /// </summary>
    public static IReadOnlyList<DisplayOutput> Outputs()
    {
        var outputs = new List<DisplayOutput>();
        nint factory = CreateFactory();
        try
        {
            for (int i = 0; TryEnumAdapter(factory, i, out var adapter); i++)
            {
                try
                {
                    AdapterDesc1 adapterDesc;
                    Check("IDXGIAdapter1::GetDesc1", ((delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)Slot(adapter, AdapterGetDesc1))(adapter, &adapterDesc));
                    for (int o = 0; TryEnumOutput(adapter, o, out var output); o++)
                    {
                        try
                        {
                            OutputDesc desc;
                            Check("IDXGIOutput::GetDesc", ((delegate* unmanaged[Stdcall]<nint, OutputDesc*, int>)Slot(output, OutputGetDesc))(output, &desc));
                            if (desc.AttachedToDesktop != 0)
                            {
                                outputs.Add(new DisplayOutput(i, o, adapterDesc.VendorId, new string(adapterDesc.Description), new string(desc.DeviceName),
                                    desc.Left, desc.Top, desc.Right - desc.Left, desc.Bottom - desc.Top));
                            }
                        }
                        finally
                        {
                            ReleaseCom(output);
                        }
                    }
                }
                finally
                {
                    ReleaseCom(adapter);
                }
            }
        }
        finally
        {
            ReleaseCom(factory);
        }

        return outputs;
    }

    /// <summary>Output <paramref name="index"/> of adapter <paramref name="adapterIndex"/>, an IDXGIOutput* the caller releases.</summary>
    public static nint OpenOutput(int adapterIndex, int index)
    {
        nint factory = CreateFactory();
        try
        {
            if (!TryEnumAdapter(factory, adapterIndex, out var adapter))
            {
                throw new ArgumentOutOfRangeException(nameof(adapterIndex), adapterIndex, "No such adapter.");
            }

            try
            {
                return TryEnumOutput(adapter, index, out var output)
                    ? output
                    : throw new ArgumentOutOfRangeException(nameof(index), index, "No such output.");
            }
            finally
            {
                ReleaseCom(adapter);
            }
        }
        finally
        {
            ReleaseCom(factory);
        }
    }

    /// <summary>Creates a D3D11 device on adapter <paramref name="index"/>; the caller releases it with <see cref="ReleaseCom"/>.</summary>
    public static nint CreateDevice(int index)
    {
        nint factory = CreateFactory();
        try
        {
            if (!TryEnumAdapter(factory, index, out var adapter))
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "No such adapter.");
            }

            try
            {
                nint device = 0;
                int level = 0;
                Check("D3D11CreateDevice", D3D11CreateDevice(adapter, DriverTypeUnknown, 0, CreateDeviceBgraSupport, null, 0, SdkVersion, &device, &level, null));
                return device;
            }
            finally
            {
                ReleaseCom(adapter);
            }
        }
        finally
        {
            ReleaseCom(factory);
        }
    }

    /// <summary>
    /// Makes <paramref name="device"/>'s immediate context safe to share: an encoder submits work on it from its own
    /// threads while capture copies frames on another.
    /// </summary>
    public static void ProtectMultithreaded(nint device)
    {
        nint multithread = QueryInterface(device, IidMultithread);
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(multithread, MultithreadSetProtected))(multithread, 1);
        }
        finally
        {
            ReleaseCom(multithread);
        }
    }

    /// <summary>The device's immediate context, an ID3D11DeviceContext* the caller releases.</summary>
    public static nint ImmediateContext(nint device)
    {
        nint context = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)Slot(device, DeviceGetImmediateContext))(device, &context);
        return context;
    }

    /// <summary>Queues a GPU copy of one whole texture into another of the same size and format.</summary>
    public static void CopyResource(nint context, nint destination, nint source) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, ContextCopyResource))(context, destination, source);

    /// <summary>D3D11_BOX: a region of a texture, right and bottom exclusive.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Box(int left, int top, int right, int bottom)
    {
        public uint Left = (uint)left;
        public uint Top = (uint)top;
        public uint Front = 0;
        public uint Right = (uint)right;
        public uint Bottom = (uint)bottom;
        public uint Back = 1;
    }

    /// <summary>A BGRA texture the CPU can read, for copying small regions of the GPU's frames into; the caller releases it.</summary>
    public static nint CreateStagingTexture(nint device, int width, int height)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = FormatB8G8R8A8Unorm,
            SampleCount = 1,
            Usage = UsageStaging,
            CpuAccessFlags = CpuAccessRead,
        };
        nint texture = 0;
        Check("ID3D11Device::CreateTexture2D", ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void*, nint*, int>)Slot(device, DeviceCreateTexture2D))(device, &desc, null, &texture));
        return texture;
    }

    /// <summary>Queues a copy of <paramref name="region"/> of <paramref name="source"/> to the top-left of <paramref name="destination"/>.</summary>
    public static void CopyRegion(nint context, nint destination, nint source, Box region) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Slot(context, ContextCopySubresourceRegion))(context, destination, 0, 0, 0, 0, source, 0, &region);

    /// <summary>Queues an upload of BGRA <paramref name="pixels"/> (rows <paramref name="rowPitch"/> bytes apart) into <paramref name="region"/> of a texture.</summary>
    public static void Upload(nint context, nint texture, Box region, ReadOnlySpan<byte> pixels, int rowPitch)
    {
        fixed (byte* data = pixels)
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, Box*, void*, uint, uint, void>)Slot(context, ContextUpdateSubresource))(context, texture, 0, &region, data, (uint)rowPitch, 0);
        }
    }

    /// <summary>
    /// Maps a staging texture for reading, waiting for the GPU to finish writing it, and hands its pixels to
    /// <paramref name="read"/> with their row pitch.
    /// </summary>
    public static T ReadStaging<T>(nint context, nint texture, int height, Func<ReadOnlySpan<byte>, int, T> read)
    {
        MappedSubresource mapped;
        Check("ID3D11DeviceContext::Map", ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, MappedSubresource*, int>)Slot(context, ContextMap))(context, texture, 0, MapRead, 0, &mapped));
        try
        {
            return read(new ReadOnlySpan<byte>(mapped.Data, (int)(mapped.RowPitch * height)), (int)mapped.RowPitch);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, ContextUnmap))(context, texture, 0);
        }
    }

    /// <summary>D3D11_MAPPED_SUBRESOURCE.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    public static Texture2DDesc TextureDesc(nint texture)
    {
        Texture2DDesc desc;
        ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)Slot(texture, Texture2DGetDesc))(texture, &desc);
        return desc;
    }

    /// <summary>The <paramref name="iid"/> interface of <paramref name="instance"/>, which the caller releases.</summary>
    public static nint QueryInterface(nint instance, Guid iid)
    {
        nint result = 0;
        Check("IUnknown::QueryInterface", ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, QueryInterfaceSlot))(instance, &iid, &result));
        return result;
    }

    public static void ReleaseCom(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, Release))(instance);
        }
    }

    static nint CreateFactory()
    {
        nint factory = 0;
        var iid = IidFactory1;
        Check("CreateDXGIFactory1", CreateDXGIFactory1(&iid, &factory));
        return factory;
    }

    static bool TryEnumAdapter(nint factory, int index, out nint adapter)
    {
        nint found = 0;
        int result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, FactoryEnumAdapters1))(factory, (uint)index, &found);
        adapter = found;
        if (result == NotFound)
        {
            return false;
        }

        Check("IDXGIFactory1::EnumAdapters1", result);
        return true;
    }

    static bool TryEnumOutput(nint adapter, int index, out nint output)
    {
        nint found = 0;
        int result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, AdapterEnumOutputs))(adapter, (uint)index, &found);
        output = found;
        if (result == NotFound)
        {
            return false;
        }

        Check("IDXGIAdapter::EnumOutputs", result);
        return true;
    }

    public static void Check(string call, int result)
    {
        if (result < 0)
        {
            throw new COMException($"{call} failed", result);
        }
    }

    public static void* Slot(nint instance, int index) => (*(void***)instance)[index];

    [DllImport("dxgi.dll", ExactSpelling = true)]
    static extern int CreateDXGIFactory1(Guid* riid, nint* factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* featureLevels, uint featureLevelCount,
        uint sdkVersion, nint* device, int* featureLevel, nint* immediateContext);
}

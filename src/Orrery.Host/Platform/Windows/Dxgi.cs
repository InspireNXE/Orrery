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
/// The adapters DXGI sees, and a D3D11 device on one of them: what capture and the encoders run on. Calls DXGI's COM
/// interfaces through their vtables, as WebFrames does with WebView2.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class Dxgi
{
    static readonly Guid IidFactory1 = new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

    const int Release = 2;
    const int FactoryEnumAdapters1 = 12;
    const int AdapterGetDesc1 = 10;
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

    static void Check(string call, int result)
    {
        if (result < 0)
        {
            throw new COMException($"{call} failed", result);
        }
    }

    static void* Slot(nint instance, int index) => (*(void***)instance)[index];

    [DllImport("dxgi.dll", ExactSpelling = true)]
    static extern int CreateDXGIFactory1(Guid* riid, nint* factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* featureLevels, uint featureLevelCount,
        uint sdkVersion, nint* device, int* featureLevel, nint* immediateContext);
}

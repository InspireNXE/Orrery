using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Host.Encoding.Amf;

/// <summary>An AMF call failed with <see cref="Result"/>.</summary>
sealed class AmfException(string call, AMF_RESULT result) : Exception($"{call} failed: {result}")
{
    public AMF_RESULT Result { get; } = result;
}

/// <summary>
/// AMD's Advanced Media Framework, from the driver's own runtime (amfrt64.dll): nothing of AMD's ships with Orrery
/// Host. Built against AMF 1.4.36 (<see cref="Amf.FullVersion"/>); slots, layouts and names are generated from the
/// headers (AmfInterop.g.cs). AMF objects are C++ interfaces, called here through their vtables as WebFrames does with
/// WebView2. Windows only so far: on Linux AMF works through Vulkan.
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe class AmfRuntime
{
    readonly nint _factory;

    AmfRuntime(nint factory, ulong runtimeVersion)
    {
        _factory = factory;
        RuntimeVersion = runtimeVersion;
    }

    /// <summary>The driver's AMF version, as AMF_MAKE_FULL_VERSION packs it.</summary>
    public ulong RuntimeVersion { get; }

    public static string FormatVersion(ulong version) => $"{version >> 48}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}";

    /// <summary>
    /// Loads the driver's AMF runtime, or returns null with a sentence saying why not. The runtime stays loaded for the
    /// life of the process, and its factory isn't reference-counted.
    /// </summary>
    public static AmfRuntime? TryLoad(out string? reason)
    {
        if (!NativeLibrary.TryLoad(Amf.DllName, out var library))
        {
            reason = "no AMD driver with AMF is installed";
            return null;
        }

        if (!NativeLibrary.TryGetExport(library, Amf.QueryVersionFunction, out var queryVersion)
            || !NativeLibrary.TryGetExport(library, Amf.InitFunction, out var init))
        {
            reason = $"{Amf.DllName} doesn't export the AMF entry points";
            return null;
        }

        ulong runtimeVersion;
        var result = ((delegate* unmanaged[Cdecl]<ulong*, AMF_RESULT>)queryVersion)(&runtimeVersion);
        if (result != AMF_RESULT.AMF_OK)
        {
            reason = $"AMFQueryVersion failed: {result}";
            return null;
        }

        if (runtimeVersion < Amf.FullVersion)
        {
            reason = $"the driver has AMF {FormatVersion(runtimeVersion)}; Orrery Host needs {FormatVersion(Amf.FullVersion)} or later";
            return null;
        }

        nint factory = 0;
        result = ((delegate* unmanaged[Cdecl]<ulong, nint*, AMF_RESULT>)init)(Amf.FullVersion, &factory);
        if (result != AMF_RESULT.AMF_OK)
        {
            reason = $"AMFInit failed: {result}";
            return null;
        }

        reason = null;
        return new AmfRuntime(factory, runtimeVersion);
    }

    /// <summary>Creates a context on <paramref name="d3d11Device"/> (an ID3D11Device*), which must be on an AMD adapter.</summary>
    public AmfContext CreateContext(nint d3d11Device)
    {
        nint context = 0;
        AmfCom.Check("AMFFactory::CreateContext", ((delegate* unmanaged[Stdcall]<nint, nint*, AMF_RESULT>)AmfCom.Slot(_factory, AmfSlot.FactoryCreateContext))(_factory, &context));
        try
        {
            AmfCom.Check("AMFContext::InitDX11", ((delegate* unmanaged[Stdcall]<nint, nint, int, AMF_RESULT>)AmfCom.Slot(context, AmfSlot.ContextInitDX11))(context, d3d11Device, Amf.Dx11_0));
        }
        catch
        {
            AmfCom.Release(context);
            throw;
        }

        return new AmfContext(this, context);
    }

    internal nint CreateComponent(nint context, string id)
    {
        nint component = 0;
        fixed (char* name = id)
        {
            AmfCom.Check($"AMFFactory::CreateComponent({id})", ((delegate* unmanaged[Stdcall]<nint, nint, char*, nint*, AMF_RESULT>)AmfCom.Slot(_factory, AmfSlot.FactoryCreateComponent))(_factory, context, name, &component));
        }

        return component;
    }
}

/// <summary>Calls shared by every AMF interface.</summary>
static unsafe class AmfCom
{
    public static void* Slot(nint instance, int index) => (*(void***)instance)[index];

    public static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(instance, AmfSlot.Release))(instance);
        }
    }

    public static nint QueryInterface(nint instance, Guid iid)
    {
        nint result = 0;
        Check("AMFInterface::QueryInterface", ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, AMF_RESULT>)Slot(instance, AmfSlot.QueryInterface))(instance, &iid, &result));
        return result;
    }

    public static void Check(string call, AMF_RESULT result)
    {
        if (result != AMF_RESULT.AMF_OK)
        {
            throw new AmfException(call, result);
        }
    }
}

/// <summary>An AMF context on one D3D11 device: makes surfaces and components.</summary>
[SupportedOSPlatform("windows")]
sealed unsafe class AmfContext : IDisposable
{
    readonly AmfRuntime _runtime;
    nint _context;

    internal AmfContext(AmfRuntime runtime, nint context)
    {
        _runtime = runtime;
        _context = context;
    }

    public AmfComponent CreateComponent(string id) => new(_runtime.CreateComponent(_context, id));

    /// <summary>A GPU surface AMF allocates on the context's device; the caller releases it with <see cref="AmfCom.Release"/>.</summary>
    public nint AllocSurface(int format, int width, int height)
    {
        nint surface = 0;
        AmfCom.Check("AMFContext::AllocSurface", ((delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint*, AMF_RESULT>)AmfCom.Slot(_context, AmfSlot.ContextAllocSurface))(_context, Amf.MemoryDx11, format, width, height, &surface));
        return surface;
    }

    public void Dispose()
    {
        if (_context != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, AMF_RESULT>)AmfCom.Slot(_context, AmfSlot.ContextTerminate))(_context);
            AmfCom.Release(_context);
            _context = 0;
        }
    }
}

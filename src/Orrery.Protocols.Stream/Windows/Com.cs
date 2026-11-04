using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Protocols.Stream.Windows;

/// <summary>
/// Calls through COM vtables, as WebFrames does with WebView2: slot numbers come from WindowsInterop.g.cs, generated
/// from the Windows SDK by tools/windows-interop.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class Com
{
    public static void* Slot(nint instance, int index) => (*(void***)instance)[index];

    public static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, Windows.Slot.Release))(instance);
        }
    }

    /// <summary>Releases <paramref name="instance"/> and clears it.</summary>
    public static void Release(ref nint instance)
    {
        Release(instance);
        instance = 0;
    }

    /// <summary>The <paramref name="iid"/> interface of <paramref name="instance"/>, which the caller releases.</summary>
    public static nint QueryInterface(nint instance, Guid iid)
    {
        nint result = 0;
        Check("IUnknown::QueryInterface", ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, Windows.Slot.QueryInterface))(instance, &iid, &result));
        return result;
    }

    public static void Check(string call, int result)
    {
        if (result < 0)
        {
            throw new COMException($"{call} failed (0x{result:X8})", result);
        }
    }
}

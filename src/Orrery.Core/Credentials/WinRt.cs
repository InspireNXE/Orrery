using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Core.Credentials;

/// <summary>
/// Just enough of the Windows Runtime ABI to call a few WinRT classes from a plain net10.0 build, without a Windows
/// target framework or CsWinRT (docs/design-biometric-unlock.md §6.1). Objects are raw COM pointers, and methods are
/// called by their vtable slot as declared in the Windows SDK's .idl files: slots 0–2 are IUnknown and 3–5 IInspectable,
/// so an interface's own methods start at 6.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class WinRt
{
    static readonly Guid IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    static readonly Guid ICryptographicBufferStatics = new("320B7E22-3CB0-4CDF-8663-1D28910065EB");
    const string CryptographicBuffer = "Windows.Security.Cryptography.CryptographicBuffer";

    // IAsyncInfo: Id, Status, ErrorCode, Cancel, Close. IAsyncOperation<T> and IAsyncAction: Completed (put, get), GetResults.
    const int AsyncStatusSlot = 7, AsyncErrorSlot = 8, AsyncCancelSlot = 9, GetResultsSlot = 8;
    const int Started = 0, Canceled = 2, Error = 3;

    /// <summary>
    /// Runs <paramref name="work"/> on a new thread in the multithreaded apartment, where WinRT objects can be used and
    /// waited on without a message loop.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            int initialised = RoInitialize(1); // RO_INIT_MULTITHREADED
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
            finally
            {
                if (initialised >= 0) RoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "Orrery WinRT",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return done.Task;
    }

    /// <summary>The address of method <paramref name="slot"/> of the interface <paramref name="obj"/> points at.</summary>
    public static void* Method(IntPtr obj, int slot) => (*(void***)obj)[slot];

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Method(obj, 2))(obj);
        }
    }

    /// <summary>The object's <paramref name="iid"/> interface, or zero when it doesn't have one (an older Windows).</summary>
    public static IntPtr TryQuery(IntPtr obj, Guid iid)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Method(obj, 0))(obj, &iid, &result);
        return hr >= 0 ? result : IntPtr.Zero;
    }

    /// <summary>A runtime class's statics, as interface <paramref name="iid"/>.</summary>
    public static IntPtr GetFactory(string className, Guid iid)
    {
        var name = CreateString(className);
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out var factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    /// <summary>As <see cref="GetFactory"/>, or zero when this Windows doesn't have that interface.</summary>
    public static IntPtr TryGetFactory(string className, Guid iid)
    {
        var name = CreateString(className);
        try
        {
            return RoGetActivationFactory(name, ref iid, out var factory) >= 0 ? factory : IntPtr.Zero;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    public static IntPtr CreateString(string value)
    {
        Check(WindowsCreateString(value, value.Length, out var hstring));
        return hstring;
    }

    public static void DeleteString(IntPtr hstring) => WindowsDeleteString(hstring);

    /// <summary>Calls a method shaped <c>HRESULT Method(T** value)</c>: a property getter or a call with no arguments.</summary>
    public static IntPtr GetObject(IntPtr obj, int slot)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Method(obj, slot))(obj, &result));
        return result;
    }

    /// <summary>Calls a getter for a 32-bit value, such as an enum.</summary>
    public static int GetInt32(IntPtr obj, int slot)
    {
        int result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Method(obj, slot))(obj, &result));
        return result;
    }

    /// <summary>
    /// Waits for an IAsyncOperation or IAsyncAction to finish by polling its status, cancelling it after
    /// <paramref name="timeout"/>. Calls <paramref name="whileWaiting"/> on each poll.
    /// </summary>
    /// <exception cref="OperationCanceledException">It was cancelled.</exception>
    /// <exception cref="TimeoutException"></exception>
    public static void Wait(IntPtr operation, TimeSpan timeout, Action? whileWaiting = null)
    {
        var info = TryQuery(operation, IAsyncInfo);
        if (info == IntPtr.Zero)
        {
            throw new InvalidOperationException("Not a Windows Runtime async operation.");
        }

        try
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            int status;
            while ((status = GetInt32(info, AsyncStatusSlot)) == Started)
            {
                if (Environment.TickCount64 > deadline)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Method(info, AsyncCancelSlot))(info);
                    throw new TimeoutException("Windows didn't answer in time.");
                }

                whileWaiting?.Invoke();
                Thread.Sleep(25);
            }

            if (status == Canceled)
            {
                throw new OperationCanceledException();
            }

            if (status == Error)
            {
                int error = GetInt32(info, AsyncErrorSlot);
                Check(error < 0 ? error : unchecked((int)0x80004005)); // E_FAIL if it gives no code
            }
        }
        finally
        {
            Release(info);
        }
    }

    /// <summary>What a finished IAsyncOperation&lt;T&gt; produced, for an object T.</summary>
    public static IntPtr GetObjectResult(IntPtr operation) => GetObject(operation, GetResultsSlot);

    /// <summary>What a finished IAsyncOperation&lt;boolean&gt; produced.</summary>
    public static bool GetBooleanResult(IntPtr operation)
    {
        byte result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Method(operation, GetResultsSlot))(operation, &result));
        return result != 0;
    }

    /// <summary>Checks a finished IAsyncAction succeeded.</summary>
    public static void GetActionResult(IntPtr action) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Method(action, GetResultsSlot))(action));

    /// <summary>A new IBuffer holding a copy of <paramref name="bytes"/>.</summary>
    public static IntPtr CreateBuffer(byte[] bytes)
    {
        var statics = GetFactory(CryptographicBuffer, ICryptographicBufferStatics);
        try
        {
            IntPtr buffer;
            fixed (byte* data = bytes)
            {
                // CreateFromByteArray(UINT32 size, BYTE* value, IBuffer** buffer)
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, IntPtr*, int>)Method(statics, 9))(statics, (uint)bytes.Length, data, &buffer));
            }

            return buffer;
        }
        finally
        {
            Release(statics);
        }
    }

    /// <summary>A copy of what an IBuffer holds.</summary>
    public static byte[] ReadBuffer(IntPtr buffer)
    {
        var statics = GetFactory(CryptographicBuffer, ICryptographicBufferStatics);
        uint length;
        byte* data = null;
        try
        {
            // CopyToByteArray(IBuffer* buffer, UINT32* size, BYTE** value); the array comes from CoTaskMemAlloc.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, byte**, int>)Method(statics, 10))(statics, buffer, &length, &data));
            var copy = new byte[length];
            new ReadOnlySpan<byte>(data, (int)length).CopyTo(copy);
            new Span<byte>(data, (int)length).Clear();
            return copy;
        }
        finally
        {
            if (data is not null) Marshal.FreeCoTaskMem((IntPtr)data);
            Release(statics);
        }
    }

    /// <summary>
    /// The WinRT WindowId of a top-level window, for the "ForWindow" calls added in Windows 11 24H2; zero when there's
    /// no window or this Windows doesn't have the conversion.
    /// </summary>
    public static ulong WindowIdFor(nint window)
    {
        if (window == 0 || !NativeLibrary.TryLoad("Windows.UI.dll", out var library)
            || !NativeLibrary.TryGetExport(library, "GetWindowIdFromWindow", out var convert))
        {
            return 0;
        }

        ulong id;
        return ((delegate* unmanaged[Stdcall]<nint, ulong*, int>)convert)(window, &id) >= 0 ? id : 0;
    }

    [DllImport("combase.dll")]
    static extern int RoInitialize(int initType);

    [DllImport("combase.dll")]
    static extern void RoUninitialize();

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}

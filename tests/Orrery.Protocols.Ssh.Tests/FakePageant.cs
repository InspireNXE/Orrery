using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Orrery.Protocols.Ssh.Tests;

/// <summary>
/// A window named "Pageant" that answers WM_COPYDATA requests the way Pageant does, passing them to a
/// <see cref="FakeAgent"/>. It records who owns each shared-memory block, which real Pageant checks.
/// </summary>
sealed class FakePageant : IDisposable
{
    const int WM_COPYDATA = 0x004A, WM_CLOSE = 0x0010;
    const uint CopyDataId = 0x804E50BA, FILE_MAP_ALL_ACCESS = 0xF001F;

    readonly FakeAgent _agent;
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly WndProc _proc;
    IntPtr _window;

    public List<string?> BlockOwners { get; } = [];

    public FakePageant(FakeAgent agent)
    {
        _agent = agent;
        _proc = OnMessage;
        _thread = new Thread(Run) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    void Run()
    {
        var windowClass = new WndClass { Size = Marshal.SizeOf<WndClass>(), WndProc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = GetModuleHandle(null), ClassName = "Pageant" };
        // The class is Unicode, so DefWindowProc must be too, or the title is read as ANSI and comes out "P".
        RegisterClassEx(ref windowClass);
        _window = CreateWindowEx(0, "Pageant", "Pageant", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        _ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            DispatchMessage(ref message);
        }
    }

    IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_CLOSE)
        {
            DestroyWindow(window);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        if (message != WM_COPYDATA)
        {
            return DefWindowProc(window, message, wParam, lParam);
        }

        var copyData = Marshal.PtrToStructure<CopyDataStruct>(lParam);
        if (copyData.Id != CopyDataId)
        {
            return IntPtr.Zero;
        }

        var name = Marshal.PtrToStringAnsi(copyData.Data)!;
        var mapping = OpenFileMapping(FILE_MAP_ALL_ACCESS, false, name);
        if (mapping == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            lock (BlockOwners) BlockOwners.Add(Owner(mapping));
            var view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0);
            var header = new byte[4];
            Marshal.Copy(view, header, 0, 4);
            var request = new byte[4 + BinaryPrimitives.ReadUInt32BigEndian(header)];
            Marshal.Copy(view, request, 0, request.Length);
            var reply = _agent.Handle(request);
            Marshal.Copy(reply, 0, view, reply.Length);
            UnmapViewOfFile(view);
            return new IntPtr(1);
        }
        finally
        {
            CloseHandle(mapping);
        }
    }

    static string? Owner(IntPtr handle)
    {
        if (GetSecurityInfo(handle, 6 /* SE_KERNEL_OBJECT */, 1 /* OWNER */, out var owner, out _, out _, out _, out var descriptor) != 0)
        {
            return null;
        }

        var sid = new SecurityIdentifier(owner).Value;
        LocalFree(descriptor);
        return sid;
    }

    public void Dispose()
    {
        PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(5));
        UnregisterClass("Pageant", GetModuleHandle(null));
    }

    delegate IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct CopyDataStruct { public nuint Id; public int Size; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClass
    {
        public int Size; public uint Style; public IntPtr WndProc; public int ClassExtra; public int WindowExtra; public IntPtr Instance;
        public IntPtr Icon; public IntPtr Cursor; public IntPtr Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct Msg { public IntPtr Window; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WndClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, nuint bytes);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
}

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Orrery.Protocols.Ssh.Agent;

/// <summary>
/// PuTTY's Pageant: the request goes in a named shared-memory block, and a WM_COPYDATA message to Pageant's
/// window names the block; Pageant writes the reply into the same block. Pageant only answers a block owned
/// by the same user, so the block is created with this user as its owner, as PuTTY does.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class PageantTransport : IAgentTransport
{
    const string WindowName = "Pageant";
    const int MaxMessage = 8192;
    const uint CopyDataId = 0x804E50BA;
    const int WM_COPYDATA = 0x004A;
    const uint PAGE_READWRITE = 0x04, FILE_MAP_WRITE = 0x0002;

    // One request at a time: the block's name comes from the thread id, and Pageant handles one message at a time.
    static readonly object Gate = new();

    public static bool IsRunning() => FindWindow(WindowName, WindowName) != IntPtr.Zero;

    public byte[] Exchange(byte[] request)
    {
        if (request.Length > MaxMessage)
        {
            throw new IOException("The request is too large for Pageant.");
        }

        lock (Gate)
        {
            var window = FindWindow(WindowName, WindowName);
            if (window == IntPtr.Zero)
            {
                throw new IOException("Pageant isn't running.");
            }

            var mapName = $"PageantRequest{GetCurrentThreadId():x8}";
            var security = OwnedByCurrentUser(out var descriptor);
            var mapping = CreateFileMapping(new IntPtr(-1), ref security, PAGE_READWRITE, 0, MaxMessage, mapName);
            LocalFree(descriptor);
            if (mapping == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't create the memory block for Pageant.");
            }

            try
            {
                var view = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, MaxMessage);
                if (view == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't map the memory block for Pageant.");
                }

                try
                {
                    Marshal.Copy(request, 0, view, request.Length);
                    var nameBytes = Encoding.ASCII.GetBytes(mapName + "\0");
                    var nameHandle = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);
                    try
                    {
                        var copyData = new CopyDataStruct { Id = CopyDataId, Size = nameBytes.Length, Data = nameHandle.AddrOfPinnedObject() };
                        if (SendMessage(window, WM_COPYDATA, IntPtr.Zero, ref copyData) == IntPtr.Zero)
                        {
                            throw new IOException("Pageant refused the request.");
                        }
                    }
                    finally
                    {
                        nameHandle.Free();
                    }

                    var header = new byte[4];
                    Marshal.Copy(view, header, 0, 4);
                    int size = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (size is <= 0 or > MaxMessage - 4)
                    {
                        throw new IOException("Pageant's reply has an impossible length.");
                    }

                    var reply = new byte[4 + size];
                    Marshal.Copy(view, reply, 0, reply.Length);
                    return reply;
                }
                finally
                {
                    UnmapViewOfFile(view);
                }
            }
            finally
            {
                CloseHandle(mapping);
            }
        }
    }

    /// <summary>Security attributes whose descriptor names this user as owner. Free <paramref name="descriptor"/> after use.</summary>
    static SecurityAttributes OwnedByCurrentUser(out IntPtr descriptor)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sddl = $"O:{identity.User!.Value}";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't describe the memory block's owner.");
        }

        return new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor, InheritHandle = false };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CopyDataStruct
    {
        public nuint Id;
        public int Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref CopyDataStruct lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr CreateFileMapping(IntPtr file, ref SecurityAttributes attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll")]
    static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
}

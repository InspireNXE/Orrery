using System.Reflection;
using System.Runtime.InteropServices;

namespace Orrery.Protocols.Local;

/// <summary>
/// The libc calls a Unix pty needs, with the constants that differ between Linux and macOS. Only functions with
/// fixed arguments are called the plain way: Apple's arm64 ABI passes variadic arguments on the stack, so ioctl
/// has a declaration of its own there. Not yet run on Linux or macOS.
/// </summary>
static unsafe class UnixNative
{
    const string Libc = "libc";

    static UnixNative() => NativeLibrary.SetDllImportResolver(typeof(UnixNative).Assembly, Resolve);

    /// <summary>"libc" by its real name: glibc's libc.so.6 (dlopen of plain libc.so finds a linker script), or libSystem.</summary>
    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Libc)
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in OperatingSystem.IsMacOS() ? ["/usr/lib/libSystem.B.dylib"] : new[] { "libc.so.6", "libc.so" })
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    static bool Mac => OperatingSystem.IsMacOS();

    public static int OpenPtFlags => Mac ? 0x0002 | 0x20000 : 0x0002 | 0x0100 | 0x80000;
    public static int ORdWr => 0x0002;
    public static short SpawnFlags => (short)(Mac
        ? 0x0004 | 0x0008 | 0x0400 | 0x4000 // SETSIGDEF, SETSIGMASK, SETSID, CLOEXEC_DEFAULT
        : 0x0004 | 0x0008 | 0x0080);        // SETSIGDEF, SETSIGMASK, SETSID (glibc 2.26 and later, musl)
    static nuint TiocSWinSz => Mac ? 0x80087467 : 0x5414;
    public static int EIntr => 4;
    public static int EAgain => Mac ? 35 : 11;
    public const int SigHup = 1, SigKill = 9;
    public const short PollIn = 0x1, PollErr = 0x8, PollHup = 0x10, PollNval = 0x20;

    // Larger than posix_spawnattr_t, posix_spawn_file_actions_t and sigset_t on any of them (glibc's are the biggest).
    public const int OpaqueSize = 1024;

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort Rows, Columns, XPixels, YPixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events, Revents;
    }

    public static int SetWindowSize(int fd, int columns, int rows)
    {
        var size = new WinSize { Columns = (ushort)Math.Clamp(columns, 1, ushort.MaxValue), Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue) };
        return Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ioctl_apple_arm64(fd, TiocSWinSz, 0, 0, 0, 0, 0, 0, ref size)
            : ioctl(fd, TiocSWinSz, ref size);
    }

    /// <summary>The shell in this user's passwd entry, or null.</summary>
    public static string? AccountShell()
    {
        try
        {
            var entry = getpwuid(getuid());
            if (entry == IntPtr.Zero)
            {
                return null;
            }

            // struct passwd: pw_shell comes after name, passwd, uid, gid, gecos and dir on Linux; macOS adds
            // pw_change and pw_class before gecos.
            int offset = Mac ? 7 * IntPtr.Size : 5 * IntPtr.Size;
            return Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry, offset));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A null-terminated array of UTF-8 strings, for argv and envp. Free it with <see cref="FreeStringArray"/>.</summary>
    public static IntPtr StringArray(IReadOnlyList<string> strings)
    {
        var array = Marshal.AllocHGlobal((strings.Count + 1) * IntPtr.Size);
        for (int i = 0; i < strings.Count; i++)
        {
            Marshal.WriteIntPtr(array, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(strings[i]));
        }

        Marshal.WriteIntPtr(array, strings.Count * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    public static void FreeStringArray(IntPtr array)
    {
        for (int i = 0; Marshal.ReadIntPtr(array, i * IntPtr.Size) is var item && item != IntPtr.Zero; i++)
        {
            Marshal.FreeCoTaskMem(item);
        }

        Marshal.FreeHGlobal(array);
    }

    public static IntPtr Opaque()
    {
        var block = Marshal.AllocHGlobal(OpaqueSize);
        new Span<byte>((void*)block, OpaqueSize).Clear();
        return block;
    }

    [DllImport(Libc, SetLastError = true)]
    public static extern int posix_openpt(int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int grantpt(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern int unlockpt(int fd);

    [DllImport(Libc)]
    public static extern int ptsname_r(int fd, byte[] buffer, nuint length);

    [DllImport(Libc, SetLastError = true)]
    static extern int ioctl(int fd, nuint request, ref WinSize size);

    // The six unused register arguments push the real one onto the stack, where Apple's arm64 varargs look for it.
    [DllImport(Libc, SetLastError = true, EntryPoint = "ioctl")]
    static extern int ioctl_apple_arm64(int fd, nuint request, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, ref WinSize size);

    [DllImport(Libc, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern nint read(int fd, byte* buffer, nuint count);

    [DllImport(Libc, SetLastError = true)]
    public static extern nint write(int fd, byte* buffer, nuint count);

    [DllImport(Libc, SetLastError = true)]
    public static extern int poll(ref PollFd fds, nuint count, int timeout);

    [DllImport(Libc, SetLastError = true)]
    public static extern int kill(int pid, int signal);

    [DllImport(Libc, SetLastError = true)]
    public static extern int waitpid(int pid, out int status, int options);

    [DllImport(Libc)]
    static extern uint getuid();

    [DllImport(Libc)]
    static extern IntPtr getpwuid(uint uid);

    [DllImport(Libc)]
    public static extern int sigemptyset(IntPtr set);

    [DllImport(Libc)]
    public static extern int sigfillset(IntPtr set);

    [DllImport(Libc)]
    public static extern int posix_spawnattr_init(IntPtr attributes);

    [DllImport(Libc)]
    public static extern int posix_spawnattr_destroy(IntPtr attributes);

    [DllImport(Libc)]
    public static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

    [DllImport(Libc)]
    public static extern int posix_spawnattr_setsigmask(IntPtr attributes, IntPtr mask);

    [DllImport(Libc)]
    public static extern int posix_spawnattr_setsigdefault(IntPtr attributes, IntPtr signals);

    [DllImport(Libc)]
    public static extern int posix_spawn_file_actions_init(IntPtr actions);

    [DllImport(Libc)]
    public static extern int posix_spawn_file_actions_destroy(IntPtr actions);

    [DllImport(Libc)]
    public static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, string path, int flags, int mode);

    [DllImport(Libc)]
    public static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);

    [DllImport(Libc)]
    public static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, string path);

    [DllImport(Libc)]
    public static extern int posix_spawn(out int pid, string path, IntPtr actions, IntPtr attributes, IntPtr argv, IntPtr envp);
}

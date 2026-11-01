using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Orrery.Protocols.Local;

/// <summary>
/// A shell a local terminal can run: <see cref="Program"/> started with <see cref="Arguments"/>. <see cref="Id"/>
/// is what settings.json keeps ("pwsh", "cmd", "wsl:Ubuntu", "login"); <see cref="Name"/> is what the user sees.
/// </summary>
/// <param name="Login">Unix: start it as a login shell, with a dash before its name in argv[0].</param>
public sealed record LocalShell(string Id, string Name, string Program, IReadOnlyList<string> Arguments, bool Login = false);

/// <summary>
/// The shells on this computer. On Windows: PowerShell 7 when it's installed, Windows PowerShell, cmd, and each
/// WSL distribution. On Linux and macOS: the user's login shell.
/// </summary>
public static class LocalShells
{
    public const string PowerShell7 = "pwsh", WindowsPowerShell = "powershell", Cmd = "cmd", LoginShell = "login";
    const string WslPrefix = "wsl:";

    /// <summary>Every shell found, the default first.</summary>
    public static IReadOnlyList<LocalShell> Available() =>
        OperatingSystem.IsWindows() ? WindowsShells() : [UnixLoginShell()];

    /// <summary>
    /// The shell saved as <paramref name="id"/>, or the default (PowerShell 7, else Windows PowerShell; the
    /// login shell on Unix) when there's no choice or the chosen one has gone, such as an uninstalled distribution.
    /// </summary>
    public static LocalShell Find(string? id)
    {
        var shells = Available();
        return shells.FirstOrDefault(s => s.Id == id) ?? shells[0];
    }

    // ---- Windows ----

    [SupportedOSPlatform("windows")]
    static List<LocalShell> WindowsShells()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var shells = new List<LocalShell>();
        if (FindPwsh() is { } pwsh)
        {
            shells.Add(new LocalShell(PowerShell7, "PowerShell 7", pwsh, ["-NoLogo"]));
        }

        shells.Add(new LocalShell(WindowsPowerShell, "Windows PowerShell", Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoLogo"]));
        shells.Add(new LocalShell(Cmd, "Command Prompt", Path.Combine(system, "cmd.exe"), []));

        var wsl = Path.Combine(system, "wsl.exe");
        if (File.Exists(wsl))
        {
            foreach (var distribution in WslDistributions())
            {
                shells.Add(new LocalShell(WslPrefix + distribution, $"{distribution} (WSL)", wsl, ["-d", distribution, "--cd", "~"]));
            }
        }

        return shells;
    }

    /// <summary>PowerShell 7 from its usual install folder, else wherever PATH has it (the Store version's alias).</summary>
    [SupportedOSPlatform("windows")]
    static string? FindPwsh()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "pwsh.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters a path can't hold.
            }
        }

        return null;
    }

    /// <summary>
    /// The installed WSL distributions, from the registry rather than wsl.exe, which can be slow to answer. Docker
    /// Desktop's own distributions aren't shells anyone opens, so they're left out, as Windows Terminal does.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static IEnumerable<string> WslDistributions()
    {
        var names = new List<string>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            foreach (var key in lxss?.GetSubKeyNames() ?? [])
            {
                using var distribution = lxss!.OpenSubKey(key);
                if (distribution?.GetValue("DistributionName") is string name && name.Length > 0
                    && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // No WSL to offer, then.
        }

        return names.Order(StringComparer.OrdinalIgnoreCase);
    }

    // ---- Linux and macOS ----

    /// <summary>
    /// The shell in the user's account (the passwd entry), else $SHELL, else /bin/sh. macOS terminals start it as a
    /// login shell, so .zprofile runs; Linux ones don't.
    /// </summary>
    static LocalShell UnixLoginShell()
    {
        var program = new[] { UnixNative.AccountShell(), Environment.GetEnvironmentVariable("SHELL") }
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) && File.Exists(p)) ?? "/bin/sh";
        return new LocalShell(LoginShell, $"Login shell ({Path.GetFileName(program)})", program, [], Login: OperatingSystem.IsMacOS());
    }
}

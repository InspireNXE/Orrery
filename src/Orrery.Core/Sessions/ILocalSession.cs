namespace Orrery.Core.Sessions;

/// <summary>
/// A session on this computer, such as a local shell: it starts a program rather than connecting, so the terminal
/// says "Starting" and shows how the program ended instead of a host and port.
/// </summary>
public interface ILocalSession
{
    /// <summary>What's running, as the user knows it, e.g. "PowerShell 7".</summary>
    string ProgramName { get; }

    /// <summary>The program's exit code once it has exited by itself; null while it runs or when Orrery ended it.</summary>
    int? ExitCode { get; }
}

using Microsoft.Extensions.Logging;

namespace Orrery.Protocols.Rdp;

/// <summary>
/// IronRDP's own log, for tracking down a problem inside it (sound that stops, say). Off unless the IRONRDP_LOG
/// environment variable is set, to a filter such as <c>ironrdp_rdpsnd=debug,ironrdp_rdpsnd_native=debug</c> or
/// <c>debug</c>; it then goes to <c>ironrdp.log</c> in Orrery's log folder, or to IRONRDP_LOG_PATH when that's set.
/// On Linux and macOS set IRONRDP_LOG_PATH too: .NET's change to its own environment doesn't reach native code there.
/// </summary>
public static class IronRdpLog
{
    const string FilterVariable = "IRONRDP_LOG", PathVariable = "IRONRDP_LOG_PATH";

    public static void StartIfAsked(string logDir)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FilterVariable)))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable)))
        {
            Environment.SetEnvironmentVariable(PathVariable, Path.Combine(logDir, "ironrdp.log"));
        }

        var logger = Orrery.Core.Diagnostics.Log.For("Rdp");
        try
        {
            Devolutions.IronRdp.Log.InitWithEnv();
            logger.LogInformation("IronRDP's log is on ({Filter}), in {Path}",
                Environment.GetEnvironmentVariable(FilterVariable), Environment.GetEnvironmentVariable(PathVariable));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            logger.LogWarning("IronRDP's log couldn't be turned on: {Message}", e.Message);
        }
    }
}

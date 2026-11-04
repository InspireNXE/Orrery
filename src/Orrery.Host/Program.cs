using Orrery.Streaming;

namespace Orrery.Host;

/// <summary>Orrery Host's entry point. Nothing to host yet: capture and encoding arrive in phase 1 (docs/design-stream.md).</summary>
static class Program
{
    static int Main()
    {
        Console.WriteLine($"Orrery Host {typeof(Program).Assembly.GetName().Version}, OSP version {OspProtocol.Version}: not implemented yet.");
        return 1;
    }
}

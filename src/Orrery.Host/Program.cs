using Orrery.Streaming;

namespace Orrery.Host;

/// <summary>Orrery Host's entry point. Only the encoder probe so far: capture and encoding arrive in phase 1 (docs/design-stream.md).</summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args is ["--probe"])
        {
            return EncoderProbe.Run(Console.Out);
        }

        Console.WriteLine($"Orrery Host {typeof(Program).Assembly.GetName().Version}, OSP version {OspProtocol.Version}: not implemented yet.");
        Console.WriteLine("  --probe   list this machine's GPUs and what each can encode");
        return 1;
    }
}

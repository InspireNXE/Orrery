using Orrery.Streaming;

namespace Orrery.Host;

/// <summary>Orrery Host's entry point. Phase 1 tools so far (docs/design-stream.md): nothing streams yet.</summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args is ["--probe"])
        {
            return EncoderProbe.Run(Console.Out);
        }

        if (args is ["--capture", .. var captureArgs])
        {
            return CaptureTest.Run(Console.Out, captureArgs);
        }

        Console.WriteLine($"Orrery Host {typeof(Program).Assembly.GetName().Version}, OSP version {OspProtocol.Version}: not implemented yet.");
        Console.WriteLine("  --probe     list this machine's GPUs and what each can encode");
        Console.WriteLine("  --capture   capture a monitor and encode it, timing each frame (AMD only so far)");
        Console.WriteLine("              [--monitor N] [--codec av1|hevc|h264] [--seconds N] [--mbps N] [--out file] [--loopback]");
        return 1;
    }
}

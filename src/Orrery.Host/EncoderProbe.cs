using Orrery.Host.Encoding.Nvenc;
using Orrery.Host.Platform.Windows;

namespace Orrery.Host;

/// <summary>
/// <c>OrreryHost --probe</c>: lists this machine's GPUs and what each can encode, so a host can be checked before the
/// streaming parts exist. NVENC only so far; AMF follows in the phase 1 spike.
/// </summary>
static class EncoderProbe
{
    public static int Run(TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("The probe runs on Windows only so far.");
            return 1;
        }

        var nvenc = NvEncApi.TryLoad(out var nvencMissing);
        output.WriteLine(nvenc is null
            ? $"NVENC: unavailable, {nvencMissing}."
            : $"NVENC: driver supports API {NvEncApi.FormatVersion(nvenc.DriverVersion)}; this build uses {NvEncApi.FormatVersion(NvEncApi.RequiredVersion)}.");

        foreach (var adapter in Dxgi.Adapters())
        {
            if (adapter.Software)
            {
                continue;
            }

            output.WriteLine();
            output.WriteLine($"{adapter.Index}: {adapter.Name} ({adapter.Vendor}, {adapter.DedicatedVideoMemory / (1024 * 1024)} MB)");
            switch (adapter.VendorId)
            {
                case GpuAdapter.Nvidia when nvenc is not null:
                    ProbeNvenc(output, nvenc, adapter);
                    break;
                case GpuAdapter.Nvidia:
                    output.WriteLine("   NVENC unavailable (see above).");
                    break;
                case GpuAdapter.Amd:
                    output.WriteLine("   AMF: not probed yet.");
                    break;
                default:
                    output.WriteLine("   No encoder support yet.");
                    break;
            }
        }

        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void ProbeNvenc(TextWriter output, NvEncApi api, GpuAdapter adapter)
    {
        nint device = 0;
        try
        {
            device = Dxgi.CreateDevice(adapter.Index);
            using var session = NvEncSession.Open(api, device);
            foreach (var codec in session.Codecs())
            {
                var caps = session.Caps(codec);
                output.WriteLine($"   {NvEncSession.CodecName(codec),-5} up to {caps.MaxWidth}x{caps.MaxHeight}, {caps.Engines} engine(s)"
                    + $", 10-bit {YesNo(caps.TenBit)}, 4:4:4 {YesNo(caps.Yuv444)}"
                    + $", intra refresh {YesNo(caps.IntraRefresh)}, reference invalidation {YesNo(caps.RefPicInvalidation)}");
            }
        }
        catch (Exception e) when (e is NvEncException or System.Runtime.InteropServices.COMException)
        {
            output.WriteLine($"   NVENC: {e.Message}");
        }
        finally
        {
            Dxgi.ReleaseCom(device);
        }
    }

    static string YesNo(bool value) => value ? "yes" : "no";
}

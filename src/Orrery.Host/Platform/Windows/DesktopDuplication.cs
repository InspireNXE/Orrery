using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Host.Platform.Windows;

/// <summary>What <see cref="DesktopDuplication.Acquire"/> got.</summary>
enum DuplicationResult
{
    /// <summary>A new desktop image; copy it, then call <see cref="DesktopDuplication.ReleaseFrame"/>.</summary>
    Frame,

    /// <summary>Only the pointer moved or changed shape; release it like a frame.</summary>
    PointerOnly,

    /// <summary>Nothing changed within the timeout. Nothing to release.</summary>
    Timeout,

    /// <summary>
    /// The duplication stopped working (a mode change, a full-screen app taking the output, the secure desktop) and must
    /// be opened again. Nothing to release.
    /// </summary>
    AccessLost,
}

/// <summary>
/// DXGI Desktop Duplication of one monitor: each changed desktop image as a D3D11 texture on the GPU that owns the
/// monitor, without the pointer, which arrives separately. Calls DXGI through its vtables, as <see cref="Dxgi"/> does.
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe class DesktopDuplication : IDisposable
{
    static readonly Guid IidOutput1 = new(0x00cddea8, 0x939b, 0x4b83, 0xa3, 0x40, 0xa6, 0x85, 0x22, 0x66, 0x66, 0xcc);

    // Slots checked against the Windows SDK's C vtable structs.
    const int Output1DuplicateOutput = 22;
    const int DuplicationGetDesc = 7;
    const int DuplicationAcquireNextFrame = 8;
    const int DuplicationReleaseFrame = 14;

    const int WaitTimeout = unchecked((int)0x887A0027);
    const int AccessLostResult = unchecked((int)0x887A0026);

    /// <summary>DXGI_OUTDUPL_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct DuplicationDesc
    {
        public uint Width;
        public uint Height;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
        public uint Rotation;
        public int DesktopImageInSystemMemory;
    }

    /// <summary>DXGI_OUTDUPL_FRAME_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX;
        public int PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    nint _duplication;

    DesktopDuplication(nint duplication, DuplicationDesc desc)
    {
        _duplication = duplication;
        Width = (int)desc.Width;
        Height = (int)desc.Height;
        RefreshRate = desc.RefreshDenominator == 0 ? 60 : (double)desc.RefreshNumerator / desc.RefreshDenominator;
        Format = desc.Format;
        Rotated = desc.Rotation is not (0 or 1); // DXGI_MODE_ROTATION_UNSPECIFIED, _IDENTITY
    }

    public int Width { get; }

    public int Height { get; }

    public double RefreshRate { get; }

    /// <summary>The DXGI_FORMAT of each frame: B8G8R8A8_UNORM (87) from DuplicateOutput.</summary>
    public uint Format { get; }

    /// <summary>The monitor is rotated, and frames arrive unrotated.</summary>
    public bool Rotated { get; }

    /// <summary>
    /// Starts duplicating <paramref name="output"/> on <paramref name="device"/>, which must be a D3D11 device on the
    /// adapter the monitor is connected to.
    /// </summary>
    public static DesktopDuplication Open(nint device, DisplayOutput output)
    {
        nint dxgiOutput = Dxgi.OpenOutput(output.AdapterIndex, output.OutputIndex);
        nint output1 = 0;
        try
        {
            output1 = Dxgi.QueryInterface(dxgiOutput, IidOutput1);
            nint duplication = 0;
            Dxgi.Check("IDXGIOutput1::DuplicateOutput", ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Dxgi.Slot(output1, Output1DuplicateOutput))(output1, device, &duplication));
            DuplicationDesc desc;
            ((delegate* unmanaged[Stdcall]<nint, DuplicationDesc*, void>)Dxgi.Slot(duplication, DuplicationGetDesc))(duplication, &desc);
            return new DesktopDuplication(duplication, desc);
        }
        finally
        {
            Dxgi.ReleaseCom(output1);
            Dxgi.ReleaseCom(dxgiOutput);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMilliseconds"/> for the desktop to change. On <see cref="DuplicationResult.Frame"/>,
    /// <paramref name="texture"/> is the image (an ID3D11Texture2D* the caller releases, valid until
    /// <see cref="ReleaseFrame"/>) and <paramref name="presentTime"/> the QueryPerformanceCounter time it was presented,
    /// which <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> shares on Windows.
    /// </summary>
    public DuplicationResult Acquire(int timeoutMilliseconds, out nint texture, out long presentTime)
    {
        texture = 0;
        presentTime = 0;
        FrameInfo info;
        nint resource = 0;
        int result = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)Dxgi.Slot(_duplication, DuplicationAcquireNextFrame))(_duplication, (uint)timeoutMilliseconds, &info, &resource);
        if (result == WaitTimeout)
        {
            return DuplicationResult.Timeout;
        }

        if (result == AccessLostResult)
        {
            return DuplicationResult.AccessLost;
        }

        Dxgi.Check("IDXGIOutputDuplication::AcquireNextFrame", result);
        try
        {
            // A zero present time means only the pointer changed.
            if (info.LastPresentTime == 0)
            {
                return DuplicationResult.PointerOnly;
            }

            texture = Dxgi.QueryInterface(resource, Dxgi.IidTexture2D);
            presentTime = info.LastPresentTime;
            return DuplicationResult.Frame;
        }
        finally
        {
            Dxgi.ReleaseCom(resource);
        }
    }

    /// <summary>Hands the last frame back to DXGI; call it as soon as the frame is copied.</summary>
    public void ReleaseFrame()
    {
        int result = ((delegate* unmanaged[Stdcall]<nint, int>)Dxgi.Slot(_duplication, DuplicationReleaseFrame))(_duplication);
        if (result != AccessLostResult)
        {
            Dxgi.Check("IDXGIOutputDuplication::ReleaseFrame", result);
        }
    }

    public void Dispose()
    {
        Dxgi.ReleaseCom(_duplication);
        _duplication = 0;
    }
}

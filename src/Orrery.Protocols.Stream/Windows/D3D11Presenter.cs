using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Protocols.Stream.Windows;

/// <summary>
/// Shows decoded frames in a window without them leaving the GPU: D3D11's video processor converts each NV12 frame
/// to RGB straight into a flip-model swapchain's back buffer, which is presented at once, tearing allowed where
/// Windows supports it so a frame never waits for the next refresh. The back buffer is the video's size and the
/// swapchain stretches it to the window.
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe class D3D11Presenter : IDisposable
{
    readonly nint _device;
    readonly nint _hwnd;
    readonly Dictionary<(nint Texture, int Slice), nint> _inputViews = [];
    nint _videoDevice;
    nint _videoContext;
    nint _swapChain;
    nint _enumerator;
    nint _processor;
    nint _outputView;
    int _width;
    int _height;
    bool _tearing;

    /// <summary>Presents into <paramref name="hwnd"/> with <paramref name="device"/>, the device the decoder uses.</summary>
    public D3D11Presenter(nint device, nint hwnd)
    {
        _device = device;
        _hwnd = hwnd;
        nint context = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)Com.Slot(device, Slot.DeviceGetImmediateContext))(device, &context);
        try
        {
            _videoDevice = Com.QueryInterface(device, Iid.VideoDevice);
            _videoContext = Com.QueryInterface(context, Iid.VideoContext);
        }
        finally
        {
            Com.Release(context);
        }
    }

    /// <summary>Whether frames are presented without waiting for the display's refresh.</summary>
    public bool Tearing => _tearing;

    /// <summary>
    /// Creates a D3D11 device on the default adapter that can decode and present: video support for DXVA and the video
    /// processor, BGRA for the swapchain, and multithread protection since Media Foundation uses it from its own
    /// threads. The caller releases it with <see cref="Com.Release(nint)"/>.
    /// </summary>
    public static nint CreateDevice()
    {
        nint device = 0;
        int level;
        Com.Check("D3D11CreateDevice", D3D11CreateDevice(0, Win.D3DDriverTypeHardware, 0,
            (uint)(Win.D3D11CreateDeviceBgraSupport | Win.D3D11CreateDeviceVideoSupport), null, 0, Win.D3D11SdkVersion, &device, &level, null));
        nint multithread = 0;
        try
        {
            multithread = Com.QueryInterface(device, Iid.Multithread);
            ((delegate* unmanaged[Stdcall]<nint, int, int>)Com.Slot(multithread, Slot.MultithreadSetMultithreadProtected))(multithread, 1);
        }
        catch
        {
            Com.Release(device);
            throw;
        }
        finally
        {
            Com.Release(multithread);
        }

        return device;
    }

    public void Present(DecodedFrame frame)
    {
        if (frame.Width != _width || frame.Height != _height || _swapChain == 0)
        {
            Recreate(frame.Width, frame.Height);
        }

        var stream = new VideoProcessorStream { Enable = 1, InputSurface = InputView(frame.Texture, frame.Subresource) };
        Com.Check("ID3D11VideoContext::VideoProcessorBlt", ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, VideoProcessorStream*, int>)Com.Slot(_videoContext, Slot.VideoContextVideoProcessorBlt))(_videoContext, _processor, _outputView, 0, 1, &stream));
        Com.Check("IDXGISwapChain::Present", ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Com.Slot(_swapChain, Slot.SwapChainPresent))(_swapChain, 0, _tearing ? (uint)Win.DxgiPresentAllowTearing : 0));
    }

    nint InputView(nint texture, int slice)
    {
        if (_inputViews.TryGetValue((texture, slice), out var view))
        {
            return view;
        }

        var desc = new VideoProcessorInputViewDesc { ViewDimension = Win.VpivDimensionTexture2D, ArraySlice = (uint)slice };
        Com.Check("ID3D11VideoDevice::CreateVideoProcessorInputView", ((delegate* unmanaged[Stdcall]<nint, nint, nint, VideoProcessorInputViewDesc*, nint*, int>)Com.Slot(_videoDevice, Slot.VideoDeviceCreateVideoProcessorInputView))(_videoDevice, texture, _enumerator, &desc, &view));
        _inputViews.Add((texture, slice), view);
        return view;
    }

    /// <summary>Makes the swapchain and video processor for frames of <paramref name="width"/> x <paramref name="height"/>.</summary>
    void Recreate(int width, int height)
    {
        ReleaseSizeDependent();
        _width = width;
        _height = height;

        var content = new VideoProcessorContentDesc
        {
            InputFrameFormat = Win.VideoFrameFormatProgressive,
            InputFrameRateNumerator = 60,
            InputFrameRateDenominator = 1,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRateNumerator = 60,
            OutputFrameRateDenominator = 1,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = Win.VideoUsageOptimalSpeed,
        };
        fixed (nint* enumerator = &_enumerator, processor = &_processor)
        {
            Com.Check("ID3D11VideoDevice::CreateVideoProcessorEnumerator", ((delegate* unmanaged[Stdcall]<nint, VideoProcessorContentDesc*, nint*, int>)Com.Slot(_videoDevice, Slot.VideoDeviceCreateVideoProcessorEnumerator))(_videoDevice, &content, enumerator));
            Com.Check("ID3D11VideoDevice::CreateVideoProcessor", ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, int>)Com.Slot(_videoDevice, Slot.VideoDeviceCreateVideoProcessor))(_videoDevice, _enumerator, 0, processor));
        }

        // The encoders send BT.709 in limited range (16-235); the swapchain wants full-range RGB.
        uint input = Win.ColorSpaceBt709Limited, output = Win.ColorSpaceRgbFull;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint*, void>)Com.Slot(_videoContext, Slot.VideoContextVideoProcessorSetStreamColorSpace))(_videoContext, _processor, 0, &input);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint*, void>)Com.Slot(_videoContext, Slot.VideoContextVideoProcessorSetOutputColorSpace))(_videoContext, _processor, &output);
        // No denoising, edge enhancement or other driver extras: they cost time and the picture is meant as sent.
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, void>)Com.Slot(_videoContext, Slot.VideoContextVideoProcessorSetStreamAutoProcessingMode))(_videoContext, _processor, 0, 0);

        nint factory = Factory();
        try
        {
            _tearing = TearingSupported(factory);
            var desc = new SwapChainDesc1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Win.DxgiFormatB8G8R8A8Unorm,
                SampleCount = 1,
                BufferUsage = (uint)Win.DxgiUsageRenderTargetOutput,
                BufferCount = 2,
                Scaling = Win.DxgiScalingStretch,
                SwapEffect = Win.DxgiSwapEffectFlipDiscard,
                Flags = _tearing ? (uint)Win.DxgiSwapChainFlagAllowTearing : 0,
            };
            fixed (nint* swapChain = &_swapChain)
            {
                Com.Check("IDXGIFactory2::CreateSwapChainForHwnd", ((delegate* unmanaged[Stdcall]<nint, nint, nint, SwapChainDesc1*, nint, nint, nint*, int>)Com.Slot(factory, Slot.Factory2CreateSwapChainForHwnd))(factory, _device, _hwnd, &desc, 0, 0, swapChain));
            }
        }
        finally
        {
            Com.Release(factory);
        }

        // With a flip-model swapchain in D3D11, buffer 0 is always the one to draw into next.
        nint backBuffer = 0;
        try
        {
            var iid = Iid.Texture2D;
            Com.Check("IDXGISwapChain::GetBuffer", ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Com.Slot(_swapChain, Slot.SwapChainGetBuffer))(_swapChain, 0, &iid, &backBuffer));
            var viewDesc = new VideoProcessorOutputViewDesc { ViewDimension = Win.VpovDimensionTexture2D };
            fixed (nint* outputView = &_outputView)
            {
                Com.Check("ID3D11VideoDevice::CreateVideoProcessorOutputView", ((delegate* unmanaged[Stdcall]<nint, nint, nint, VideoProcessorOutputViewDesc*, nint*, int>)Com.Slot(_videoDevice, Slot.VideoDeviceCreateVideoProcessorOutputView))(_videoDevice, backBuffer, _enumerator, &viewDesc, outputView));
            }
        }
        finally
        {
            Com.Release(backBuffer);
        }
    }

    /// <summary>The DXGI factory that made the device's adapter, which the swapchain must come from.</summary>
    nint Factory()
    {
        nint dxgiDevice = 0, adapter = 0, factory = 0;
        try
        {
            dxgiDevice = Com.QueryInterface(_device, Iid.DXGIDevice);
            Com.Check("IDXGIDevice::GetAdapter", ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(dxgiDevice, Slot.DXGIDeviceGetAdapter))(dxgiDevice, &adapter));
            var iid = Iid.Factory2;
            Com.Check("IDXGIObject::GetParent", ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Slot(adapter, Slot.DXGIObjectGetParent))(adapter, &iid, &factory));
            return factory;
        }
        finally
        {
            Com.Release(adapter);
            Com.Release(dxgiDevice);
        }
    }

    static bool TearingSupported(nint factory)
    {
        nint factory5 = 0;
        try
        {
            factory5 = Com.QueryInterface(factory, Iid.Factory5);
            int allowed = 0;
            return ((delegate* unmanaged[Stdcall]<nint, int, int*, uint, int>)Com.Slot(factory5, Slot.Factory5CheckFeatureSupport))(factory5, Win.DxgiFeaturePresentAllowTearing, &allowed, sizeof(int)) >= 0
                && allowed != 0;
        }
        catch (COMException)
        {
            return false; // DXGI before 1.5
        }
        finally
        {
            Com.Release(factory5);
        }
    }

    void ReleaseSizeDependent()
    {
        foreach (var view in _inputViews.Values)
        {
            Com.Release(view);
        }

        _inputViews.Clear();
        Com.Release(ref _outputView);
        Com.Release(ref _swapChain);
        Com.Release(ref _processor);
        Com.Release(ref _enumerator);
    }

    public void Dispose()
    {
        ReleaseSizeDependent();
        Com.Release(ref _videoContext);
        Com.Release(ref _videoDevice);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* featureLevels, uint featureLevelCount,
        uint sdkVersion, nint* device, int* featureLevel, nint* immediateContext);
}

using TestD3D12.Logging;
using TestD3D12.Windowing;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace TestD3D12.Graphics;

public class D3D12Renderer : IDisposable
{
    public readonly BaseWindow Window;

    private const int FrameCount = 2;
    private uint _backbufferIndex = 0;

    public readonly IDXGIFactory4 DXGIFactory;
    private readonly ID3D12Device2 Device;
    public readonly ID3D12CommandQueue GraphicsQueue;
    public readonly IDXGISwapChain3 SwapChain;

    private bool _disposed;

    public D3D12Renderer(BaseWindow window)
    {
        Window = window;

        DXGIFactory = CreateDXGIFactory2<IDXGIFactory4>(false);

        Logger.LogInformation($"Creating device");
        if (D3D12CreateDevice(null, FeatureLevel.Level_11_0, out Device!).Failure)
        {
            Logger.LogCritical("Failed to create d3d12 device");
            throw new NotSupportedException();
        }

        GraphicsQueue = Device.CreateCommandQueue(CommandListType.Direct);
        GraphicsQueue.Name = "Graphics Queue";

        SwapChainDescription1 swapChainDesc = new()
        {
            BufferCount = FrameCount,
            Width = (uint)window.Size.X,
            Height = (uint)window.Size.Y,
            Format = Format.R8G8B8A8_UNorm,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0)
        };

        Logger.LogInformation($"Creating swapchain with size {swapChainDesc.Width}, {swapChainDesc.Height}");

        using IDXGISwapChain1 swapChain = DXGIFactory.CreateSwapChainForHwnd(GraphicsQueue, window.WindowHandle, swapChainDesc);

        if (DXGIFactory.MakeWindowAssociation(window.WindowHandle, WindowAssociationFlags.IgnoreAltEnter).Failure)
        {
            Logger.LogCritical("Failed to make window association");
        }

        SwapChain = swapChain.QueryInterface<IDXGISwapChain3>();
        _backbufferIndex = SwapChain.CurrentBackBufferIndex;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            Logger.LogInformation($"Disposing {nameof(D3D12Renderer)}");

#if DEBUG
            uint refCount = Device.Release();
            if (refCount > 0)
            {
                Logger.LogWarning($"There are {refCount} unreleased references left on the device");

                ID3D12DebugDevice? d3d12DebugDevice = Device.QueryInterfaceOrNull<ID3D12DebugDevice>();
                if (d3d12DebugDevice is not null)
                {
                    d3d12DebugDevice.ReportLiveDeviceObjects(ReportLiveDeviceObjectFlags.Detail | ReportLiveDeviceObjectFlags.IgnoreInternal);
                    d3d12DebugDevice.Dispose();
                }
            }
#else
            Device.Dispose();
#endif

            DXGIFactory.Dispose();
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

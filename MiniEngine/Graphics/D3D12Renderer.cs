using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using MiniEngine.Input;
using MiniEngine.Logging;
using MiniEngine.Platform;
using MiniEngine.Windowing;
using SDL;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace MiniEngine.Graphics;

public unsafe class D3D12Renderer : IDisposable
{
    public const int SwapChainBufferCount = 2;

    public readonly BaseWindow Window;

    public readonly IDXGIFactory4 DXGIFactory;
    public readonly ID3D12Device2 Device;
    public readonly ID3D12CommandQueue GraphicsQueue;
    public readonly IDXGISwapChain3 SwapChain;

    public readonly D3D12CopyManager CopyManager;

    private readonly ID3D12DescriptorHeap _rtvDescriptorHeap;
    private readonly uint _rtvDescriptorSize;
    private ID3D12Resource[] _renderTargets;

    private const Format PreferredDepthStencilFormat = Format.D32_Float;
    private readonly ID3D12DescriptorHeap _dsvDescriptorHeap;
    private Format _depthStencilFormat;
    private ID3D12Resource _depthStencilTexture;

    private readonly ID3D12CommandAllocator[] _commandAllocators;

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipelineState;

    private readonly ID3D12GraphicsCommandList4 _commandList;

    private readonly ID3D12Resource _vertexBuffer;
    private readonly VertexBufferView _vertexBufferView;

    private readonly ID3D12Resource _constantBuffer;
    private byte* _constantsMemory = null;

    private readonly ImGuiRenderer _imGuiRenderer;
    private readonly ImGuiController _imGuiController;

    private readonly ID3D12Fence _frameFence;
    private readonly WaitHandle _frameFenceEvent;
    private readonly ulong[] _fenceValues = new ulong[SwapChainBufferCount];
    private uint _frameIndex;

    private bool _disposed;

    public D3D12Renderer(BaseWindow window)
    {
        Window = window;

        Log.LogInfo("Creating DXGIFactory");
        DXGIFactory = CreateDXGIFactory2<IDXGIFactory4>(false);

        // Device creation
        ID3D12Device2? d3d12Device = default;

        // On windows we can normally just create a D3D12 device without specifying the adapter
        // but vkd3d doesn't determine this for us properly, and never sets device->parent,
        // so we must send it through during device creation
        //
        // I'm using a modified version of vkd3d that sets device_create_info.parent to the adapter we specify
        // https://github.com/chairclr/vkd3d-proton
        for (uint adapterIndex = 0; DXGIFactory.EnumAdapters(adapterIndex, out IDXGIAdapter? adapter).Success; adapterIndex++)
        {
            if (D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out d3d12Device).Success)
            {
                adapter.Dispose();

                Log.LogInfo($"Chosing adapter {adapterIndex} for device creation");
                break;
            }
        }

        if (d3d12Device == null)
        {
            Log.LogCrit("Failed to create D3D12Device");
            throw new NotSupportedException();
        }

        Device = d3d12Device;

        Log.LogInfo("Creating main GraphicsQueue");
        GraphicsQueue = Device.CreateCommandQueue(CommandListType.Direct);
        GraphicsQueue.Name = "Graphics Queue";

        CopyManager = new(Device, GraphicsQueue);

        CreateSwapChain(out SwapChain, out _frameIndex);

        _rtvDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, SwapChainBufferCount));
        _rtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        _dsvDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));

        CreateFrameResources(out _renderTargets);
        CreateDepthStencil(out _depthStencilTexture, out _depthStencilFormat);

        _commandAllocators = new ID3D12CommandAllocator[SwapChainBufferCount];
        for (int i = 0; i < SwapChainBufferCount; i++)
        {
            _commandAllocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        RootSignatureFlags rootSignatureFlags = RootSignatureFlags.AllowInputAssemblerInputLayout
            | RootSignatureFlags.DenyHullShaderRootAccess
            | RootSignatureFlags.DenyDomainShaderRootAccess
            | RootSignatureFlags.DenyGeometryShaderRootAccess
            | RootSignatureFlags.DenyAmplificationShaderRootAccess
            | RootSignatureFlags.DenyMeshShaderRootAccess;
        RootSignatureDescription1 rootSignatureDesc = new()
        {
            Flags = rootSignatureFlags,
            Parameters =
            [
                new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic), ShaderVisibility.Vertex),
            ],
            StaticSamplers =
            [
                // Fill with samplers in the future ;)
            ],
        };

        _rootSignature = Device.CreateRootSignature(rootSignatureDesc);

        // We actually have a separate cbuffer for each swapchain buffer
        // Later we update only the cbuffer for the current frameIndex
        uint cbufferSize = (uint)(Unsafe.SizeOf<Constants>() * D3D12Renderer.SwapChainBufferCount);
        _constantBuffer = Device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(cbufferSize),
                ResourceStates.GenericRead);

        // Map the entire _constantBuffer, and d3d stores the pointer to that in _constantsMemory
        fixed (void* pMemory = &_constantsMemory)
        {
            _constantBuffer.Map(0, pMemory);
        }

        InputElementDescription[] inputElementDescs =
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32_Float, 12, 0)
        ];

        ReadOnlyMemory<byte> triangleVS = ShaderLoader.LoadShaderBytecode("Basic/TriangleVS");
        ReadOnlyMemory<byte> trianglePS = ShaderLoader.LoadShaderBytecode("Basic/TrianglePS");

        GraphicsPipelineStateDescription psoDesc = new()
        {
            RootSignature = _rootSignature,
            VertexShader = triangleVS,
            PixelShader = trianglePS,
            InputLayout = new InputLayoutDescription(inputElementDescs),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm],
            DepthStencilFormat = _depthStencilFormat,
            SampleDescription = SampleDescription.Default
        };

        _pipelineState = Device.CreateGraphicsPipelineState(psoDesc);

        _commandList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(CommandListType.Direct, _commandAllocators[_frameIndex], _pipelineState);
        _commandList.Close();

        uint vertexBufferStride = (uint)Unsafe.SizeOf<TriangleVertex>();
        uint vertexBufferSize = 3 * vertexBufferStride;

        _vertexBuffer = Device.CreateCommittedResource(
            HeapType.Upload,
            ResourceDescription.Buffer(vertexBufferSize),
            ResourceStates.GenericRead);

        ReadOnlySpan<TriangleVertex> triangleVertices =
        [
            new TriangleVertex(new Vector3(100 + Window.Size.X / 4f, 100 + Window.Size.Y / 4f + Window.Size.Y / 4f, 0.0f), new Vector3(1.0f, 0.0f, 0.0f)),
            new TriangleVertex(new Vector3(100 + Window.Size.X / 4f + Window.Size.X / 4f, 100 + Window.Size.Y / 4f + Window.Size.Y / -4f, 0.0f), new Vector3(0.0f, 1.0f, 0.0f)),
            new TriangleVertex(new Vector3(100 + Window.Size.X / 4f + Window.Size.X / -4f, 100 + Window.Size.Y / 4f + Window.Size.Y / -4f, 0.0f), new Vector3(0.0f, 0.0f, 1.0f))
        ];

        _vertexBuffer.SetData(triangleVertices);
        // It's fine to cache it, but must be updated if we map/unmap the vertexBuffer I guess?
        _vertexBufferView = new VertexBufferView(_vertexBuffer.GPUVirtualAddress, vertexBufferSize, vertexBufferStride);

        _frameFence = Device.CreateFence(_fenceValues[_frameIndex]);
        _fenceValues[_frameIndex]++;

        _frameFenceEvent = PlatformHelper.CreateAutoResetEvent(false);

        nint imGuiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(imGuiContext);
        _imGuiRenderer = new ImGuiRenderer(this);
        _imGuiController = new ImGuiController(Window, imGuiContext);

        Stopwatch deltaTimeWatch = new();

        bool exit = false;
        SDL_Event @event = default;
        while (!exit)
        {
            deltaTimeWatch.Stop();

            float deltaTime = (float)deltaTimeWatch.Elapsed.TotalSeconds;

            if (deltaTime <= 0)
            {
                deltaTime = 1f / 60f;
            }

            deltaTimeWatch.Restart();

            ImGuiIOPtr io = ImGui.GetIO();

            io.DeltaTime = deltaTime;

            unsafe
            {
                while (SDL3.SDL_PollEvent(&@event))
                {
                    if (@event.Type == SDL_EventType.SDL_EVENT_QUIT)
                    {
                        exit = true;
                        break;
                    }

                    if (@event.Type == SDL_EventType.SDL_EVENT_WINDOW_RESIZED)
                    {
                        int w = @event.window.data1;
                        int h = @event.window.data2;

                        Resize(w, h);
                    }

                    // TODO: Figure out more stuff with input stealing
                    if (_imGuiController.HandleEvent(@event))
                    {
                        continue;
                    }
                }
            }

            _imGuiController.NewFrame();

            if (ImGui.Begin("Test Window"))
            {
                ImGui.Text($"FPS {io.Framerate}");
                ImGui.Image(0, new Vector2(512, 128));
            }
            ImGui.End();

            _imGuiController.EndFrame();

            DrawFrame();
        }
    }

    private void CreateSwapChain(out IDXGISwapChain3 swapChain3, out uint backbufferIndex)
    {
        SwapChainDescription1 swapChainDesc = new()
        {
            BufferCount = SwapChainBufferCount,
            Width = (uint)Window.Size.X,
            Height = (uint)Window.Size.Y,
            Format = Format.R8G8B8A8_UNorm,
            BufferUsage = Usage.RenderTargetOutput,
            // TODO: Maybe determine a better swap effect?
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
        };

        Log.LogInfo($"Creating swapchain with {swapChainDesc.Width}x{swapChainDesc.Height}, BufferCount = {swapChainDesc.BufferCount}");

        using IDXGISwapChain1 swapChain = DXGIFactory.CreateSwapChainForHwnd(GraphicsQueue, Window.WindowHandle, swapChainDesc);

        if (DXGIFactory.MakeWindowAssociation(Window.WindowHandle, WindowAssociationFlags.IgnoreAltEnter).Failure)
        {
            Log.LogCrit("Failed to make window association");
        }

        swapChain3 = swapChain.QueryInterface<IDXGISwapChain3>();
        backbufferIndex = swapChain3.CurrentBackBufferIndex;
    }

    private void CreateDepthStencil(out ID3D12Resource depthStencilTexture, out Format depthStencilFormat)
    {
        depthStencilFormat = PreferredDepthStencilFormat;

        SwapChainDescription1 swapChainDesc = SwapChain.Description1;

        ResourceDescription depthStencilDesc = ResourceDescription.Texture2D(
                depthStencilFormat,
                swapChainDesc.Width,
                swapChainDesc.Height,
                flags: ResourceFlags.AllowDepthStencil);

        ClearValue depthOptimizedClearValue = new(depthStencilFormat, 1.0f, 0);

        Log.LogInfo($"Creating depth stencil with {depthStencilDesc.Width}x{depthStencilDesc.Height}");

        depthStencilTexture = Device.CreateCommittedResource(
            HeapType.Default,
            depthStencilDesc,
            ResourceStates.DepthWrite,
            depthOptimizedClearValue);
        depthStencilTexture.Name = "DepthStencil Texture";

        DepthStencilViewDescription viewDesc = new()
        {
            Format = depthStencilFormat,
            ViewDimension = DepthStencilViewDimension.Texture2D
        };

        Device.CreateDepthStencilView(depthStencilTexture, viewDesc, _dsvDescriptorHeap.GetCPUDescriptorHandleForHeapStart1());
    }

    private void CreateFrameResources(out ID3D12Resource[] renderTargets)
    {
        CpuDescriptorHandle rtvHandle = _rtvDescriptorHeap.GetCPUDescriptorHandleForHeapStart1();

        // Create a RTV for buffer in the swapchain
        Log.LogInfo($"Creating {SwapChainBufferCount} rtvs");
        renderTargets = new ID3D12Resource[SwapChainBufferCount];
        for (uint i = 0; i < SwapChainBufferCount; i++)
        {
            renderTargets[i] = SwapChain.GetBuffer<ID3D12Resource>(i);
            // TODO: See if passing null here is best practice
            Device.CreateRenderTargetView(renderTargets[i], null, rtvHandle);
            rtvHandle += (int)_rtvDescriptorSize;
        }
    }

    // Handle resizing the swapchain, render target views, and depth stencil
    private void Resize(int width, int height)
    {
        Log.LogInfo($"Resizing to {width}x{height}");

        WaitIdle();

        Log.LogInfo("Disposing RTVs and depth stencil");
        for (int i = 0; i < SwapChainBufferCount; i++)
        {
            _renderTargets[i].Dispose();
        }
        _depthStencilTexture.Dispose();

        Log.LogInfo("Resizing swapchain buffers");
        SwapChain.ResizeBuffers1(SwapChainBufferCount, (uint)width, (uint)height, Format.R8G8B8A8_UNorm);
        CreateFrameResources(out _renderTargets);
        CreateDepthStencil(out _depthStencilTexture, out _depthStencilFormat);

        _frameIndex = SwapChain.CurrentBackBufferIndex;
    }

    // Waits until the gpu is idle
    // call before disposing anything or recreating any resources
    public void WaitIdle()
    {
        GraphicsQueue.Signal(_frameFence, _fenceValues[_frameIndex]);
        _frameFence.SetEventOnCompletion(_fenceValues[_frameIndex], _frameFenceEvent);
        _frameFenceEvent.WaitOne();

        _fenceValues[_frameIndex]++;
    }

    private void DrawFrame()
    {
        // Create the projection matrix and then copy it to the mapped part of memory for the current frameIndex
        // as mentioned before, every frame that can be in flight gets its own constant buffer
        //
        // This could maybe be simplified so that there's only one constant buffer, but idk; the directx samples do it like this
        Constants constants = new(Matrix4x4.CreateOrthographicOffCenter(0f, Window.Size.X, Window.Size.Y, 0.0f, -1.0f, 1.0f));
        void* dest = _constantsMemory + (Unsafe.SizeOf<Constants>() * _frameIndex);
        Unsafe.CopyBlock(dest, &constants, (uint)Unsafe.SizeOf<Constants>());

        _commandAllocators[_frameIndex].Reset();
        _commandList.Reset(_commandAllocators[_frameIndex], _pipelineState);
        _commandList.BeginEvent("Frame");

        // Set necessary state.
        _commandList.SetGraphicsRootSignature(_rootSignature);

        // Indicate that the back buffer will be used as a render target.
        _commandList.ResourceBarrierTransition(_renderTargets[_frameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

        CpuDescriptorHandle rtvDescriptor = new(_rtvDescriptorHeap.GetCPUDescriptorHandleForHeapStart1(), (int)_frameIndex, _rtvDescriptorSize);
        CpuDescriptorHandle dsvDescriptor = _dsvDescriptorHeap.GetCPUDescriptorHandleForHeapStart1();

        Color4 clearColor = Colors.CornflowerBlue;

        _commandList.OMSetRenderTargets(rtvDescriptor, dsvDescriptor);
        _commandList.ClearRenderTargetView(rtvDescriptor, clearColor);
        _commandList.ClearDepthStencilView(dsvDescriptor, ClearFlags.Depth, 1.0f, 0);

        // We directly set the constant buffer view to the current _constantBuffer[frameIndex]
        _commandList.SetGraphicsRootConstantBufferView(0, _constantBuffer.GPUVirtualAddress + (ulong)(_frameIndex * Unsafe.SizeOf<Constants>()));

        _commandList.RSSetViewport(new Viewport(Window.Size.X, Window.Size.Y));
        _commandList.RSSetScissorRect((int)Window.Size.X, (int)Window.Size.Y);

        _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _commandList.IASetVertexBuffers(0, _vertexBufferView);
        _commandList.DrawInstanced(3, 1, 0, 0);


        // Indicate that the back buffer will now be used to present.
        //_commandList.ResourceBarrierTransition(_renderTargets[_frameIndex], ResourceStates.RenderTarget, ResourceStates.Present);
        _commandList.EndEvent();

        ImGui.Render();

        _commandList.BeginEvent("ImGui");
        _commandList.OMSetRenderTargets(rtvDescriptor, null);
        _imGuiRenderer.PopulateCommandList(_commandList, _frameIndex, ImGui.GetDrawData());
        _commandList.EndEvent();

        // Indicate that the back buffer will be used to present
        _commandList.ResourceBarrierTransition(_renderTargets[_frameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

        _commandList.Close();

        // Execute the command list.
        GraphicsQueue.ExecuteCommandList(_commandList);

        Result result = SwapChain.Present(1, PresentFlags.None);

        // Device lost ??
        if (result.Failure && (result.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || result.Code == Vortice.DXGI.ResultCode.DeviceReset.Code))
        {
            //HandleDeviceLost();
            Log.LogInfo($"Lost device with code {result.Code}");
            return;
        }

        // Schedule a signal
        ulong currentFenceValue = _fenceValues[_frameIndex];
        GraphicsQueue.Signal(_frameFence, currentFenceValue);

        // Update the frame index
        _frameIndex = SwapChain.CurrentBackBufferIndex;

        // Wait until the next frame is ready to be rendered, if we need to
        if (_frameFence.CompletedValue < _fenceValues[_frameIndex])
        {
            _frameFence.SetEventOnCompletion(_fenceValues[_frameIndex], _frameFenceEvent);
            _frameFenceEvent.WaitOne();
        }

        // Set the fence value for the next frame
        _fenceValues[_frameIndex] = currentFenceValue + 1;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            WaitIdle();

            Log.LogInfo($"Disposing {nameof(D3D12Renderer)}");

            CopyManager.Dispose();
            _imGuiRenderer.Dispose();

            _constantBuffer.Dispose();
            _vertexBuffer.Dispose();
            for (int i = 0; i < SwapChainBufferCount; i++)
            {
                _commandAllocators[i].Dispose();
                _renderTargets[i].Dispose();
            }
            _depthStencilTexture.Dispose();
            _dsvDescriptorHeap?.Dispose();
            _rtvDescriptorHeap.Dispose();
            _frameFence.Dispose();
            _commandList.Dispose();
            _pipelineState.Dispose();
            _rootSignature.Dispose();
            SwapChain.Dispose();
            GraphicsQueue.Dispose();

#if DEBUG
            // Check and log any unreleased device resources
            uint refCount = Device.Release();
            if (refCount > 0)
            {
                Log.LogWarn($"There are {refCount} unreleased references left on the device");

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

    [StructLayout(LayoutKind.Sequential)]
    record struct TriangleVertex(Vector3 position, Vector3 color);

    [StructLayout(LayoutKind.Sequential)]
    private record struct Constants(Matrix4x4 ProjectionMatrix);
}
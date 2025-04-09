using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using MiniEngine.Input;
using MiniEngine.Logging;
using MiniEngine.Platform;
using MiniEngine.UI;
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

public unsafe partial class D3D12Renderer : IDisposable
{
    public const int SwapChainBufferCount = 2;

    public readonly SDLWindow Window;

    public readonly IDXGIFactory4 DXGIFactory;
    public readonly ID3D12Device5 Device;
    public readonly ID3D12CommandQueue GraphicsQueue;
    public readonly IDXGISwapChain3 SwapChain;


    public readonly D3D12CopyManager CopyManager;
    public readonly D3D12GpuTimingManager GpuTimingManager;
    public readonly ModelLoader ModelLoader;

    private readonly ID3D12DescriptorHeap _rtvDescriptorHeap;
    private readonly uint _rtvDescriptorSize;
    private ID3D12Resource[] _renderTargets;

    private const Format PreferredDepthStencilFormat = Format.D32_Float;
    private readonly ID3D12DescriptorHeap _dsvDescriptorHeap;
    private Format _depthStencilFormat;
    private ID3D12Resource _depthStencilTexture;

    private readonly ID3D12CommandAllocator[] _commandAllocators;

    private readonly ID3D12RootSignature _graphicsRootSignature;
    private readonly ID3D12PipelineState _graphicsPipelineState;


    private readonly ID3D12GraphicsCommandList4 _commandList;

    private readonly ID3D12DescriptorHeap _resourceDescriptorHeap;
    private readonly uint _resourceDescriptorSize;

    private readonly ID3D12DescriptorHeap _samplerDescriptorHeap;
    private readonly uint _samplerDescriptorSize;

    private readonly ID3D12Resource _vertexConstantBuffer;
    private byte* _vertexConstantsMemory = null;

    private readonly ID3D12Resource _pixelConstantBuffer;
    private byte* _pixelConstantsMemory = null;

    private readonly List<Mesh> _meshes = [];

    // Ray tracing stuff
    private static bool s_rayTracingSupported;
    public static bool RayTracingSupported => s_rayTracingSupported;

    private readonly ID3D12DescriptorHeap? _raytracingResourceHeap;
    private ID3D12Resource? _shadowTexture;

    private readonly ID3D12PipelineState? _shadowComputePipelineState;
    private readonly ID3D12RootSignature? _shadowComputeRootSignature;
    private readonly ID3D12DescriptorHeap? _shadowComputeResourceHeap;
    private ID3D12Resource? _shadowComputeIntermedTexture;

    // ImGui
    private readonly ImGuiRenderer _imGuiRenderer;
    private readonly ImGuiController _imGuiController;

    // Sync
    private readonly ID3D12Fence _frameFence;
    private readonly WaitHandle _frameFenceEvent;
    private readonly ulong[] _fenceValues = new ulong[SwapChainBufferCount];

    private uint _frameIndex;

    private readonly Camera _mainCamera;
    private Vector3 _firstPersonCameraRotation = Vector3.Zero;
    private Vector3 _firstPersonCameraPosition = Vector3.Zero;

    private bool _disposed;

    public D3D12Renderer(SDLWindow window)
    {
        Window = window;

        Log.LogInfo("Creating DXGIFactory");
        DXGIFactory = CreateDXGIFactory2<IDXGIFactory4>(false);

        // Device creation
        ID3D12Device5? d3d12Device = default;

        // On windows we can normally just create a D3D12 device without specifying the adapter
        // but vkd3d doesn't determine this for us properly, and never sets device->parent,
        // so we must send it through during device creation
        //
        // I'm using a modified version of vkd3d that sets device_create_info.parent to the adapter we specify
        // https://github.com/chairclr/vkd3d-proton
        for (uint adapterIndex = 0; DXGIFactory.EnumAdapters(adapterIndex, out IDXGIAdapter? adapter).Success; adapterIndex++)
        {
            if (D3D12CreateDevice(adapter, FeatureLevel.Level_12_0, out d3d12Device).Success)
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

        if (Device.Options5.RaytracingTier < RaytracingTier.Tier1_0)
        {
            Log.LogWarn("Ray tracing not supported, disabling ray tracing");
            s_rayTracingSupported = false;
        }
        else
        {
            s_rayTracingSupported = true;
        }

        Log.LogInfo("Creating main GraphicsQueue");
        GraphicsQueue = Device.CreateCommandQueue(CommandListType.Direct);
        GraphicsQueue.Name = "Graphics Queue";


        CopyManager = new(Device, GraphicsQueue);

        RootSignatureFlags graphicsRootSignatureFlags = RootSignatureFlags.AllowInputAssemblerInputLayout
            | RootSignatureFlags.DenyHullShaderRootAccess
            | RootSignatureFlags.DenyDomainShaderRootAccess
            | RootSignatureFlags.DenyGeometryShaderRootAccess
            | RootSignatureFlags.DenyAmplificationShaderRootAccess
            | RootSignatureFlags.DenyMeshShaderRootAccess;

        nint imGuiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(imGuiContext);
        _imGuiRenderer = new ImGuiRenderer(this);
        _imGuiController = new ImGuiController(Window, imGuiContext);
        Log.LogInfo("Created ImGui resources");

        // Frame resources
        {
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
        }

        _frameFence = Device.CreateFence(_fenceValues[_frameIndex]);
        _fenceValues[_frameIndex]++;

        _frameFenceEvent = PlatformHelper.CreateAutoResetEvent(false);

        _mainCamera = new Camera(90.0f, Window.AspectRatio, 0.05f, 1000.0f);

        // Normal render resources
        {
            RootDescriptorTable1 srvTable = new
            ([
                new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0),
            ]);
            RootDescriptorTable1 samplerTable = new
            ([
                new DescriptorRange1(DescriptorRangeType.Sampler, 1, 0, 0, 0),
            ]);

            RootSignatureDescription1 rootSignatureDesc = new()
            {
                Flags = graphicsRootSignatureFlags,
                Parameters =
                [
                    new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic), ShaderVisibility.Vertex),
                    new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic), ShaderVisibility.Pixel),

                    new(srvTable, ShaderVisibility.Pixel),
                    new(samplerTable, ShaderVisibility.Pixel),
                ],
                StaticSamplers =
                [
                    //new StaticSamplerDescription(SamplerDescription.LinearClamp, ShaderVisibility.Pixel, 0, 0),
                ],
            };

            _graphicsRootSignature = Device.CreateRootSignature(rootSignatureDesc);

            _resourceDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
            _resourceDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            _samplerDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.Sampler, 1, DescriptorHeapFlags.ShaderVisible));
            _samplerDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);

            CpuDescriptorHandle samplerHandle = _samplerDescriptorHeap.GetCPUDescriptorHandleForHeapStart1();
            SamplerDescription samplerDesc = SamplerDescription.LinearClamp;
            Device.CreateSampler(ref samplerDesc, new CpuDescriptorHandle(samplerHandle, 0, _samplerDescriptorSize));

            // We actually have a separate cbuffer for each swapchain buffer
            // Later we update only the cbuffer for the current frameIndex
            {
                uint cbufferSize = (uint)(Unsafe.SizeOf<VertexConstants>() * SwapChainBufferCount);
                _vertexConstantBuffer = Device.CreateCommittedResource(
                        HeapType.Upload,
                        ResourceDescription.Buffer(cbufferSize),
                        ResourceStates.GenericRead);

                // Map the entire _constantBuffer, and d3d stores the pointer to that in _constantsMemory
                fixed (void* pMemory = &_vertexConstantsMemory)
                {
                    _vertexConstantBuffer.Map(0, pMemory);
                }
            }

            {
                uint cbufferSize = (uint)(Unsafe.SizeOf<PixelConstants>() * SwapChainBufferCount);
                _pixelConstantBuffer = Device.CreateCommittedResource(
                        HeapType.Upload,
                        ResourceDescription.Buffer(cbufferSize),
                        ResourceStates.GenericRead);

                // Map the entire _constantBuffer, and d3d stores the pointer to that in _constantsMemory
                fixed (void* pMemory = &_pixelConstantsMemory)
                {
                    _pixelConstantBuffer.Map(0, pMemory);
                }
            }

            InputElementDescription[] inputElementDescs =
            [
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0)
            ];

            ReadOnlyMemory<byte> triangleVS = ShaderLoader.LoadShaderBytecode("Basic/TriangleVS");
            ReadOnlyMemory<byte> trianglePS = ShaderLoader.LoadShaderBytecode("Basic/TrianglePS");

            GraphicsPipelineStateDescription psoDesc = new()
            {
                RootSignature = _graphicsRootSignature,
                VertexShader = triangleVS,
                PixelShader = trianglePS,
                InputLayout = new InputLayoutDescription(inputElementDescs),
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = RasterizerDescription.CullClockwise,
                BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.Default,
                RenderTargetFormats = [Format.R8G8B8A8_UNorm],
                DepthStencilFormat = _depthStencilFormat,
                SampleDescription = SampleDescription.Default
            };

            _graphicsPipelineState = Device.CreateGraphicsPipelineState(psoDesc);

            _commandList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(CommandListType.Direct, _commandAllocators[_frameIndex], _graphicsPipelineState);
            _commandList.Close();
        }



        GpuTimingManager = new(Device, _commandList, GraphicsQueue);
        ModelLoader = new(Device);

        _meshes.AddRange(ModelLoader.LoadModelMeshes("Assets/Models/living_room.glb"));

        if (RayTracingSupported)
        {
            CreateShadowRayTracingState(out _raytracingRootSignature, out _rayGenRootSignature, out _hitRootSignature, out _missRootSignature, out _raytracingStateObject);
            CreateShadowRayTracingShaderBindingTable(out _raytracingStateObjectProperties, out _shaderBindingTableEntrySize, out _shaderBindingTableBuffer);

            // Build acceleration structures needed for raytracing
            _commandList.Reset(_commandAllocators[_frameIndex]);

            BuildRaytracingAccelerationStructureInputs topLevelInputs = new()
            {
                Type = RaytracingAccelerationStructureType.TopLevel,
                Flags = RaytracingAccelerationStructureBuildFlags.None,
                Layout = ElementsLayout.Array,
                DescriptorsCount = (uint)_meshes.Count,
            };

            RaytracingAccelerationStructurePrebuildInfo topLevelInfo = Device.GetRaytracingAccelerationStructurePrebuildInfo(topLevelInputs);

            if (topLevelInfo.ResultDataMaxSizeInBytes == 0)
            {
                Log.LogCrit("Failed to create top level inputs");
                throw new Exception();
            }

            ulong maxScratchSize = Math.Max(_meshes.Max(x => x.BottomLevelPrebuildInfo.ScratchDataSizeInBytes), topLevelInfo.ScratchDataSizeInBytes);

            using ID3D12Resource scratchResource = Device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Buffer(maxScratchSize, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess);

            _topLevelAccelerationStructure = Device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Buffer(topLevelInfo.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.RaytracingAccelerationStructure);

            _instanceBuffer = Device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer((ulong)(Unsafe.SizeOf<RaytracingInstanceDescription>() * _meshes.Count)),
                ResourceStates.GenericRead);

            _instanceBuffer.SetData(_meshes.Select(x => x.InstanceDescription).ToArray());

            foreach (Mesh mesh in _meshes)
            {
                mesh.BuildBottomLevelAccelerationStructure(_commandList, scratchResource);
            }

            topLevelInputs.InstanceDescriptions = _instanceBuffer.GPUVirtualAddress;

            _commandList.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
            {
                Inputs = topLevelInputs,
                ScratchAccelerationStructureData = scratchResource.GPUVirtualAddress,
                DestinationAccelerationStructureData = _topLevelAccelerationStructure.GPUVirtualAddress,
            });

            _commandList.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_topLevelAccelerationStructure)));

            _commandList.Close();

            GraphicsQueue.ExecuteCommandList(_commandList);

            WaitIdle();

            Log.LogInfo("Created raytracing acceleration structures");

            uint cbufferSize = (uint)(Unsafe.SizeOf<RaytracingConstants>() * SwapChainBufferCount);
            _raytracingConstantBuffer = Device.CreateCommittedResource(
                    HeapType.Upload,
                    ResourceDescription.Buffer(cbufferSize),
                    ResourceStates.GenericRead);

            fixed (void* pMemory = &_raytracingConstantsMemory)
            {
                _raytracingConstantBuffer.Map(0, pMemory);
            }

            _raytracingResourceHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, DescriptorHeapFlags.ShaderVisible));

            CreateShadowRaytracingResources(out _shadowTexture);

            RootDescriptorTable1 srvTable = new
            ([
                new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0, 0, 0),
                new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 2),
            ]);

            RootSignatureDescription1 rootSignatureDesc = new(RootSignatureFlags.None)
            {
                Parameters =
                [
                    new(srvTable, ShaderVisibility.All),
                ],
            };

            _shadowComputeRootSignature = Device.CreateRootSignature(rootSignatureDesc);

            ReadOnlyMemory<byte> blurCS = ShaderLoader.LoadShaderBytecode("Shadow/ShadowBlurH");

            ComputePipelineStateDescription psoDesc = new()
            {
                RootSignature = _shadowComputeRootSignature,
                ComputeShader = blurCS,
            };

            _shadowComputePipelineState = Device.CreateComputePipelineState(psoDesc);
            _shadowComputeResourceHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, DescriptorHeapFlags.ShaderVisible));

            CreateShadowComputeResources(out _shadowComputeIntermedTexture);
        }
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

            _imGuiController.NewFrame(deltaTime);
            DrawImGui();
            _imGuiController.EndFrame();

            //eltime += deltaTime;

            Update(deltaTime);

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

    private int _depthDebugImGuiViewId;

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


        // SRV for the depth view debug shader
        ShaderResourceViewDescription debugSrvDesc = new()
        {
            Format = depthStencilFormat,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new()
            {
                MipLevels = 1,
                MostDetailedMip = 0,
            },
            // Forces greyscale, maps xyzw => xxxw
            Shader4ComponentMapping = ShaderComponentMapping.Encode(
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent3),
        };

        _depthDebugImGuiViewId = _imGuiRenderer.BindTextureView(depthStencilTexture, debugSrvDesc);
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

    private int _shadowIntermedImGuiViewId;

    private void CreateShadowComputeResources(out ID3D12Resource intermedShadowTexture)
    {
        ResourceDescription intermedBufferDesc = new()
        {
            DepthOrArraySize = 1,
            Dimension = ResourceDimension.Texture2D,
            Format = Format.R16_Float,
            Flags = ResourceFlags.AllowUnorderedAccess,
            Width = (ulong)Window.Size.X,
            Height = (uint)Window.Size.Y,
            Layout = TextureLayout.Unknown,
            MipLevels = 1,
            SampleDescription = new SampleDescription(1, 0),
        };

        intermedShadowTexture = Device.CreateCommittedResource(
                    HeapType.Default,
                    intermedBufferDesc,
                    ResourceStates.CopySource);

        uint shadowResourceHeapSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        CpuDescriptorHandle heapHandle = _shadowComputeResourceHeap!.GetCPUDescriptorHandleForHeapStart1();

        UnorderedAccessViewDescription shadowTextureResourceViewDesc = new()
        {
            ViewDimension = UnorderedAccessViewDimension.Texture2D
        };
        Device.CreateUnorderedAccessView(_shadowTexture, null, shadowTextureResourceViewDesc, heapHandle);
        heapHandle += (int)shadowResourceHeapSize;

        UnorderedAccessViewDescription intermedTextureResourceViewDesc = new()
        {
            ViewDimension = UnorderedAccessViewDimension.Texture2D
        };
        Device.CreateUnorderedAccessView(intermedShadowTexture, null, intermedTextureResourceViewDesc, heapHandle);
        heapHandle += (int)shadowResourceHeapSize;

        ShaderResourceViewDescription depthSrvDesc = new()
        {
            Format = _depthStencilFormat,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new()
            {
                MipLevels = 1,
                MostDetailedMip = 0,
            }
        };
        Device.CreateShaderResourceView(_depthStencilTexture, depthSrvDesc, heapHandle);
        heapHandle += (int)shadowResourceHeapSize;

        ShaderResourceViewDescription intermedTextureSrvDesc = new()
        {
            Format = intermedBufferDesc.Format,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new()
            {
                MipLevels = 1,
                MostDetailedMip = 0,
            },
            Shader4ComponentMapping = ShaderComponentMapping.Default
        };

        CpuDescriptorHandle resourceHandle = _resourceDescriptorHeap.GetCPUDescriptorHandleForHeapStart1();
        Device.CreateShaderResourceView(intermedShadowTexture, intermedTextureSrvDesc, new(resourceHandle, 0, _resourceDescriptorSize));

        ShaderResourceViewDescription debugSrvDesc = new()
        {
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            // Forces greyscale, maps xyzw => xxxw
            Shader4ComponentMapping = ShaderComponentMapping.Encode(
                        ShaderComponentMappingSource.FromMemoryComponent0,
                        ShaderComponentMappingSource.FromMemoryComponent0,
                        ShaderComponentMappingSource.FromMemoryComponent0,
                        ShaderComponentMappingSource.FromMemoryComponent3),
        };

        _shadowIntermedImGuiViewId = _imGuiRenderer.BindTextureView(intermedShadowTexture, debugSrvDesc);
    }

    // Handle resizing the swapchain, render target views, and depth stencil
    private void Resize(int width, int height)
    {
        Log.LogInfo($"Resizing to {width}x{height}");

        WaitIdle();

        Log.LogInfo("Disposing RTVs and depth stencil");
        _imGuiRenderer.UnbindTextureView(_depthDebugImGuiViewId);

        for (int i = 0; i < SwapChainBufferCount; i++)
        {
            _renderTargets[i].Dispose();
        }
        _depthStencilTexture.Dispose();
        if (RayTracingSupported)
        {
            _imGuiRenderer.UnbindTextureView(_raytracedOcclusionImGuiViewId);
            _imGuiRenderer.UnbindTextureView(_raytracedOccluderDistanceImGuiViewId);
            _shadowTexture!.Dispose();
        }

        Log.LogInfo("Resizing swapchain buffers");
        SwapChain.ResizeBuffers1(SwapChainBufferCount, (uint)width, (uint)height, Format.R8G8B8A8_UNorm);
        CreateFrameResources(out _renderTargets);
        CreateDepthStencil(out _depthStencilTexture, out _depthStencilFormat);

        if (RayTracingSupported)
        {
            CreateShadowRaytracingResources(out _shadowTexture);
        }

        _frameIndex = SwapChain.CurrentBackBufferIndex;

        _mainCamera.SetProjection(90f, Window.AspectRatio, 0.05f, 1000.0f);
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

    private void Update(float deltaTime)
    {
        ImGuiIOPtr io = ImGui.GetIO();

        // Basic FPS camera movement/rotation
        // Click and drag to rotate, WASD/QE for directional/verticle movement
        if (!io.WantCaptureKeyboard && !io.WantCaptureMouse)
        {
            if (io.MouseDown[0])
            {
                _firstPersonCameraRotation.X += -io.MouseDelta.X * (1f / 360f);
                _firstPersonCameraRotation.Y += io.MouseDelta.Y * (1f / 360f);

                _mainCamera.Rotation = Quaternion.CreateFromYawPitchRoll(_firstPersonCameraRotation.X, _firstPersonCameraRotation.Y, _firstPersonCameraRotation.Z);
            }

            float s = 2f * deltaTime;
            if (ImGui.IsKeyDown(ImGuiKey.W))
            {
                _firstPersonCameraPosition += _mainCamera.Forward * s;
            }
            if (ImGui.IsKeyDown(ImGuiKey.S))
            {
                _firstPersonCameraPosition += _mainCamera.Backward * s;
            }

            if (ImGui.IsKeyDown(ImGuiKey.A))
            {
                _firstPersonCameraPosition += _mainCamera.Right * s;
            }
            if (ImGui.IsKeyDown(ImGuiKey.D))
            {
                _firstPersonCameraPosition += _mainCamera.Left * s;
            }

            if (ImGui.IsKeyDown(ImGuiKey.Q))
            {
                _firstPersonCameraPosition += Vector3.UnitY * s;
            }
            if (ImGui.IsKeyDown(ImGuiKey.E))
            {
                _firstPersonCameraPosition += -Vector3.UnitY * s;
            }

            _mainCamera.Position = _firstPersonCameraPosition;
        }
    }

    private void DrawImGui()
    {
        if (ImGui.Begin("Debug Window"))
        {
            ImGui.Text($"FPS: {ImGui.GetIO().Framerate}");
            if (ImGui.CollapsingHeader("Gpu Timing"))
            {
                foreach ((string name, D3D12GpuTimingManager.GpuTimer timer) in GpuTimingManager.GpuTimers)
                {
                    ImGui.Text($"{name}: {timer.TimeMs:F5}ms");
                }
            }

            if (ImGui.CollapsingHeader("Image Views"))
            {
                ImGuiExtensions.ZoomableImage("Depth Texture View", _depthDebugImGuiViewId, Vector2.Normalize(Window.Size) * 1000f, Window.Size);
                if (RayTracingSupported)
                {
                    ImGuiExtensions.ZoomableImage("Shadow Occlusion Texture View", _raytracedOcclusionImGuiViewId, Vector2.Normalize(Window.Size) * 1000f, Window.Size);
                    ImGuiExtensions.ZoomableImage("Shadow Occluder Distance View", _raytracedOccluderDistanceImGuiViewId, Vector2.Normalize(Window.Size) * 1000f, Window.Size);
                    ImGuiExtensions.ZoomableImage("Shadow Intermed View", _shadowIntermedImGuiViewId, Vector2.Normalize(Window.Size) * 1000f, Window.Size);
                }
            }
        }
        ImGui.End();
    }

    float eltime = 0;
    private void DrawFrame()
    {
        // Create the projection matrix and then copy it to the mapped part of memory for the current frameIndex
        // as mentioned before, every frame that can be in flight gets its own constant buffer
        //
        // This could maybe be simplified so that there's only one constant buffer, but idk; the directx samples do it like this
        {
            VertexConstants vertexConstants = new(_mainCamera.ViewMatrix * _mainCamera.ProjectionMatrix);
            void* dest = _vertexConstantsMemory + Unsafe.SizeOf<VertexConstants>() * _frameIndex;
            Unsafe.CopyBlock(dest, &vertexConstants, (uint)Unsafe.SizeOf<VertexConstants>());
        }

        {
            PixelConstants pixelConstants = new(eltime, Window.Size);
            void* dest = _pixelConstantsMemory + Unsafe.SizeOf<PixelConstants>() * _frameIndex;
            Unsafe.CopyBlock(dest, &pixelConstants, (uint)Unsafe.SizeOf<PixelConstants>());
        }

        if (RayTracingSupported)
        {
            if (Matrix4x4.Invert(_mainCamera.ViewMatrix * _mainCamera.ProjectionMatrix, out Matrix4x4 inverseViewProjection))
            {
                RaytracingConstants raytracingConstants = new(inverseViewProjection, eltime);
                void* dest = _raytracingConstantsMemory + Unsafe.SizeOf<RaytracingConstants>() * _frameIndex;
                Unsafe.CopyBlock(dest, &raytracingConstants, (uint)Unsafe.SizeOf<RaytracingConstants>());
            }
        }

        GpuTimingManager.NewFrame();

        _commandAllocators[_frameIndex].Reset();
        _commandList.Reset(_commandAllocators[_frameIndex], _graphicsPipelineState);

        // Indicate that the back buffer will be used as a render target.
        _commandList.ResourceBarrierTransition(_renderTargets[_frameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

        CpuDescriptorHandle rtvDescriptor = new(_rtvDescriptorHeap.GetCPUDescriptorHandleForHeapStart1(), (int)_frameIndex, _rtvDescriptorSize);
        CpuDescriptorHandle dsvDescriptor = _dsvDescriptorHeap.GetCPUDescriptorHandleForHeapStart1();

        _commandList.SetGraphicsRootSignature(_graphicsRootSignature);

        _commandList.SetMarker("Depth Prepass");
        {
            GpuTimingManager.BeginTiming("Depth Prepasss");
            _commandList.OMSetRenderTargets(null!, dsvDescriptor);
            _commandList.ClearDepthStencilView(dsvDescriptor, ClearFlags.Depth, 1.0f, 0);

            // We directly set the constant buffer view to the current _constantBuffer[frameIndex]
            _commandList.SetGraphicsRootConstantBufferView(0, _vertexConstantBuffer.GPUVirtualAddress + (ulong)(_frameIndex * Unsafe.SizeOf<VertexConstants>()));
            _commandList.SetGraphicsRootConstantBufferView(1, _pixelConstantBuffer.GPUVirtualAddress + (ulong)(_frameIndex * Unsafe.SizeOf<PixelConstants>()));

            _commandList.RSSetViewport(new Viewport(Window.Size.X, Window.Size.Y));
            _commandList.RSSetScissorRect((int)Window.Size.X, (int)Window.Size.Y);

            foreach (IRenderable renderable in _meshes)
            {
                renderable.RenderDepth(_commandList);
            }

            GpuTimingManager.EndTiming("Depth Prepasss");
        }

        _commandList.SetMarker("Ray Tracing");
        if (RayTracingSupported)
        {
            GpuTimingManager.BeginTiming("Shadow Ray Tracing");
            _commandList.SetPipelineState1(_raytracingStateObject);
            _commandList.SetComputeRootSignature(_raytracingRootSignature);
            _commandList.SetDescriptorHeaps(_raytracingResourceHeap);

            _commandList.SetComputeRootConstantBufferView(0, _raytracingConstantBuffer!.GPUVirtualAddress + (ulong)(_frameIndex * Unsafe.SizeOf<RaytracingConstants>()));

            _commandList.DispatchRays(new DispatchRaysDescription
            {
                Width = (uint)Window.Size.X,
                Height = (uint)Window.Size.Y,
                Depth = 1u,

                RayGenerationShaderRecord = new GpuVirtualAddressRange
                {
                    StartAddress = _shaderBindingTableBuffer!.GPUVirtualAddress + (ulong)_shaderBindingTableEntrySize * 0,
                    SizeInBytes = (ulong)_shaderBindingTableEntrySize,
                },

                HitGroupTable = new GpuVirtualAddressRangeAndStride
                {
                    StartAddress = _shaderBindingTableBuffer.GPUVirtualAddress + (ulong)_shaderBindingTableEntrySize * 1,
                    SizeInBytes = (ulong)_shaderBindingTableEntrySize,
                    StrideInBytes = (ulong)_shaderBindingTableEntrySize,
                },

                MissShaderTable = new GpuVirtualAddressRangeAndStride
                {
                    StartAddress = _shaderBindingTableBuffer.GPUVirtualAddress + (ulong)_shaderBindingTableEntrySize * 2,
                    SizeInBytes = (ulong)_shaderBindingTableEntrySize,
                    StrideInBytes = (ulong)_shaderBindingTableEntrySize,
                },
            });

            _commandList.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_shadowTexture)));

            GpuTimingManager.EndTiming("Shadow Ray Tracing");
            GpuTimingManager.BeginTiming("Shadow Blur");
            _commandList.SetPipelineState(_shadowComputePipelineState);
            _commandList.SetComputeRootSignature(_shadowComputeRootSignature);
            _commandList.SetDescriptorHeaps(_shadowComputeResourceHeap);

            _commandList.Dispatch((uint)Math.Ceiling(Window.Size.X / 16), (uint)Math.Ceiling(Window.Size.Y / 16), 1);

            _commandList.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_shadowComputeIntermedTexture)));

            GpuTimingManager.EndTiming("Shadow Blur");
        }

        _commandList.SetMarker("Forward");
        {
            GpuTimingManager.BeginTiming("Main Scene Forward");
            _commandList.SetPipelineState(_graphicsPipelineState);
            _commandList.SetGraphicsRootSignature(_graphicsRootSignature);

            _commandList.SetDescriptorHeaps([_resourceDescriptorHeap, _samplerDescriptorHeap]);
            _commandList.SetGraphicsRootConstantBufferView(0, _vertexConstantBuffer.GPUVirtualAddress + (ulong)(_frameIndex * Unsafe.SizeOf<VertexConstants>()));
            _commandList.SetGraphicsRootConstantBufferView(1, _pixelConstantBuffer.GPUVirtualAddress + (ulong)(_frameIndex * Unsafe.SizeOf<PixelConstants>()));

            Color4 clearColor = Colors.CornflowerBlue;

            _commandList.OMSetRenderTargets(rtvDescriptor, dsvDescriptor);
            _commandList.ClearRenderTargetView(rtvDescriptor, clearColor);

            // We directly set the constant buffer view to the current _constantBuffer[frameIndex]

            //_commandList.SetGraphicsRootDescriptorTable(1, new(_resourceDescriptorHeap.GetGPUDescriptorHandleForHeapStart1(), 0, _resourceDescriptorSize));

            _commandList.RSSetViewport(new Viewport(Window.Size.X, Window.Size.Y));
            _commandList.RSSetScissorRect((int)Window.Size.X, (int)Window.Size.Y);

            foreach (IRenderable renderable in _meshes)
            {
                renderable.Render(_commandList);
            }
            GpuTimingManager.EndTiming("Main Scene Forward");
        }

        ImGui.Render();

        GpuTimingManager.BeginTiming("ImGui");
        _commandList.OMSetRenderTargets(rtvDescriptor, null);
        _commandList.SetMarker("ImGui");
        _imGuiRenderer.PopulateCommandList(_commandList, _frameIndex, ImGui.GetDrawData());
        GpuTimingManager.EndTiming("ImGui");

        GpuTimingManager.ResolveQueue();

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

        GpuTimingManager.EndFrame();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            WaitIdle();

            Log.LogInfo($"Disposing {nameof(D3D12Renderer)}");

            _frameFenceEvent.Dispose();

            ModelLoader.Dispose();
            GpuTimingManager.Dispose();
            CopyManager.Dispose();
            _imGuiRenderer.Dispose();

            if (RayTracingSupported)
            {
                _raytracingRootSignature?.Dispose();
                _shadowTexture?.Dispose();
                _raytracingStateObject?.Dispose();
                _raytracingResourceHeap?.Dispose();
                _raytracingConstantBuffer?.Dispose();
                _raytracingStateObjectProperties?.Dispose();
                _rayGenRootSignature?.Dispose();
                _hitRootSignature?.Dispose();
                _missRootSignature?.Dispose();
                _topLevelAccelerationStructure?.Dispose();
                _instanceBuffer?.Dispose();
                _shaderBindingTableBuffer?.Dispose();
            }

            _pixelConstantBuffer.Dispose();
            _vertexConstantBuffer.Dispose();
            _samplerDescriptorHeap.Dispose();
            _resourceDescriptorHeap.Dispose();

            foreach (Mesh mesh in _meshes)
            {
                mesh.Dispose();
            }

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
            _graphicsPipelineState.Dispose();
            _graphicsRootSignature.Dispose();

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
    private record struct VertexConstants(Matrix4x4 ViewProjectionMatrix);

    [StructLayout(LayoutKind.Sequential)]
    private record struct PixelConstants(float Time, Vector2 WindowSize, float _ = default, Matrix4x3 __ = default);

    [StructLayout(LayoutKind.Sequential)]
    private record struct RaytracingConstants(Matrix4x4 InverseViewProjection, float Time, Vector3 _ = default, Matrix4x3 __ = default);
}
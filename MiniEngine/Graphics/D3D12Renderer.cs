using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using glTFLoader;
using glTFLoader.Schema;
using ImGuiNET;
using MiniEngine.Input;
using MiniEngine.Logging;
using MiniEngine.Platform;
using MiniEngine.Windowing;
using SDL;
using SharpGen.Runtime;
using Vortice;
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

    public readonly SDLWindow Window;

    public readonly IDXGIFactory4 DXGIFactory;
    public readonly ID3D12Device5 Device;
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

    private readonly ID3D12RootSignature _graphicsRootSignature;
    private readonly ID3D12PipelineState _graphicsPipelineState;


    // Debug exclusive stuff
    private readonly ID3D12RootSignature _debugRootSignature;
    private readonly ID3D12DescriptorHeap _debugResourceDescriptorHeap;
    private readonly uint _debugResourceDescriptorSize;

    private readonly ID3D12PipelineState _depthDebugPipelineState;


    private readonly ID3D12GraphicsCommandList4 _commandList;

    private readonly ID3D12Resource _vertexBuffer;
    private readonly VertexBufferView _vertexBufferView;
    private readonly ID3D12Resource _indexBuffer;
    private readonly IndexBufferView _indexBufferView;
    private readonly int _indexCount;

    private readonly ID3D12Resource _constantBuffer;
    private byte* _constantsMemory = null;

    // Ray tracing stuff
    public readonly bool RayTracingSupported;

    private readonly ID3D12RootSignature? _raytracingRootSignature;
    private readonly ID3D12RootSignature? _rayGenRootSignature;
    private readonly ID3D12RootSignature? _hitRootSignature;
    private readonly ID3D12RootSignature? _missRootSignature;
    private readonly ID3D12StateObject? _raytracingStateObject;
    private readonly ID3D12Resource? _bottomLevelAccelerationStructure;
    private readonly ID3D12Resource? _topLevelAccelerationStructure;
    private readonly ID3D12Resource? _instanceBuffer;

    private readonly ID3D12Resource? _raytracingConstantBuffer;
    private byte* _raytracingConstantsMemory = null;

    private readonly ID3D12StateObjectProperties? _raytracingStateObjectProperties;
    private readonly uint _shaderBindingTableEntrySize;
    private readonly ID3D12Resource? _shaderBindingTableBuffer;
    private readonly ID3D12DescriptorHeap? _raytracingResourceHeap;
    private ID3D12Resource? _raytracedShadowMask;

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
            RayTracingSupported = false;
        }
        else
        {
            RayTracingSupported = true;
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

        // Debug pipeline renders a fullscreen triangle and samples from a single SRV onto the screen
        {
            RootDescriptorTable1 debugSrvTable = new(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 1));
            RootSignatureDescription1 debugRootSignatureDesc = new()
            {
                Flags = graphicsRootSignatureFlags,
                Parameters =
                [
                    new(debugSrvTable, ShaderVisibility.Pixel)
                ],
                StaticSamplers =
                [
                    new StaticSamplerDescription(SamplerDescription.LinearClamp, ShaderVisibility.Pixel, 0, 0),
                ],
            };
            _debugRootSignature = Device.CreateRootSignature(debugRootSignatureDesc);
            _debugResourceDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 16, DescriptorHeapFlags.ShaderVisible));
            _debugResourceDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            // The debug sampler is the first thing in the descriptor heap
            // Everything else is srvs to textures to view stuff, which is also indexed by _debugRenderViewIndex
            SamplerDescription debugSamplerDesc = SamplerDescription.LinearClamp;
            Device.CreateSampler(ref debugSamplerDesc, _debugResourceDescriptorHeap.GetCPUDescriptorHandleForHeapStart1());

            ReadOnlyMemory<byte> debugVS = ShaderLoader.LoadShaderBytecode("Debug/FullscreenVS");
            ReadOnlyMemory<byte> depthDebugPS = ShaderLoader.LoadShaderBytecode("Debug/FullscreenDepthPS");

            GraphicsPipelineStateDescription depthDebugPsoDesc = new()
            {
                RootSignature = _debugRootSignature,
                VertexShader = debugVS,
                PixelShader = depthDebugPS,
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = [Format.R8G8B8A8_UNorm],
                SampleDescription = SampleDescription.Default
            };

            _depthDebugPipelineState = Device.CreateGraphicsPipelineState(depthDebugPsoDesc);
            Log.LogInfo("Created debug resources");
        }

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

            RootSignatureDescription1 rootSignatureDesc = new()
            {
                Flags = graphicsRootSignatureFlags,
                Parameters =
                [
                    new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic), ShaderVisibility.Vertex),
                ],
                StaticSamplers =
                [
                    // Fill with samplers in the future ;)
                ],
            };

            _graphicsRootSignature = Device.CreateRootSignature(rootSignatureDesc);
        }

        _frameFence = Device.CreateFence(_fenceValues[_frameIndex]);
        _fenceValues[_frameIndex]++;

        _frameFenceEvent = PlatformHelper.CreateAutoResetEvent(false);

        _mainCamera = new Camera(90.0f, Window.AspectRatio, 0.05f, 1000.0f);

        // Normal render resources
        {
            // We actually have a separate cbuffer for each swapchain buffer
            // Later we update only the cbuffer for the current frameIndex
            uint cbufferSize = (uint)(Unsafe.SizeOf<Constants>() * SwapChainBufferCount);
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

            Gltf model = Interface.LoadModel("Assets/Models/living_room.glb");
            Span<byte> modelData = Interface.LoadBinaryBuffer("Assets/Models/living_room.glb");

            Scene scene = model.Scenes[model.Scene ?? 0];
            glTFLoader.Schema.Node node = model.Nodes[scene.Nodes[9]];
            Mesh mesh = model.Meshes[node.Mesh!.Value];
            MeshPrimitive primitive = mesh.Primitives[0];

            // position data
            Accessor posAccessor = model.Accessors[primitive.Attributes["POSITION"]];
            BufferView posView = model.BufferViews[posAccessor.BufferView!.Value];
            int posOffset = posView.ByteOffset + posAccessor.ByteOffset;
            int posStride = posView.ByteStride ?? 12; // Vector3
            ReadOnlySpan<Vector3> posData = MemoryMarshal.Cast<byte, Vector3>(modelData[posOffset..(posOffset + (posStride * posAccessor.Count))]);

            // position data
            Accessor normalAccessor = model.Accessors[primitive.Attributes["NORMAL"]];
            BufferView normalView = model.BufferViews[normalAccessor.BufferView!.Value];
            int normalOffset = normalView.ByteOffset + normalAccessor.ByteOffset;
            int normalStride = normalView.ByteStride ?? 12; // Vector3
            ReadOnlySpan<Vector3> normalData = MemoryMarshal.Cast<byte, Vector3>(modelData[normalOffset..(normalOffset + (normalStride * normalAccessor.Count))]);

            // index data
            Accessor idxAccessor = model.Accessors[primitive.Indices!.Value];
            BufferView idxView = model.BufferViews[idxAccessor.BufferView!.Value];
            int idxOffset = idxView.ByteOffset + idxAccessor.ByteOffset;
            int idxStride = idxView.ByteStride ?? 4; // int
            ReadOnlySpan<int> idxData = MemoryMarshal.Cast<byte, int>(modelData[idxOffset..(idxOffset + (idxStride * idxAccessor.Count))]);

            Span<TriangleVertex> verts = new TriangleVertex[posAccessor.Count];
            Span<int> idxs = new int[idxAccessor.Count];

            for (int i = 0; i < posAccessor.Count; i++)
            {
                verts[i] = new TriangleVertex(posData[i], normalData[i]);
            }

            idxData.CopyTo(idxs);

            uint vertexBufferStride = (uint)Unsafe.SizeOf<TriangleVertex>();
            uint vertexBufferSize = (uint)(verts.Length * vertexBufferStride);

            _vertexBuffer = Device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(vertexBufferSize),
                ResourceStates.GenericRead);

            uint indexBufferStride = (uint)Unsafe.SizeOf<int>();
            uint indexBufferSize = (uint)(idxs.Length * indexBufferStride);

            _indexBuffer = Device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(indexBufferSize),
                ResourceStates.GenericRead);

            _vertexBuffer.SetData((ReadOnlySpan<TriangleVertex>)verts);
            // It's fine to cache it, but must be updated if we map/unmap the vertexBuffer I guess?
            _vertexBufferView = new VertexBufferView(_vertexBuffer.GPUVirtualAddress, vertexBufferSize, vertexBufferStride);

            _indexBuffer.SetData((ReadOnlySpan<int>)idxs);
            _indexBufferView = new IndexBufferView(_indexBuffer.GPUVirtualAddress, indexBufferSize, Format.R32_UInt);

            _indexCount = idxs.Length;
        }

        if (RayTracingSupported)
        {
            RootSignatureDescription1 raytracingRootSignatureDesc = new(RootSignatureFlags.None)
            {
                Parameters =
                [
                    new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic), ShaderVisibility.All),
                ]
            };

            _raytracingRootSignature = Device.CreateRootSignature(raytracingRootSignatureDesc);

            // Descriptor table combining both SRV and UAV
            RootDescriptorTable1 descriptorTable = new(
            [
                // Output shadow mask
                new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 0, 0, 0), 

                // Input scene bvh and then depth texture
                new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, 0, 0, 1),
            ]);

            RootSignatureDescription1 rayGenSignatureDesc = new(RootSignatureFlags.LocalRootSignature)
            {
                Parameters =
                [
                    // Shader resources
                    new(descriptorTable, ShaderVisibility.All),
                ]
            };

            _rayGenRootSignature = Device.CreateRootSignature(rayGenSignatureDesc);

            RootSignatureDescription1 hitRootSignatureDesc = new(RootSignatureFlags.LocalRootSignature);
            _hitRootSignature = Device.CreateRootSignature(hitRootSignatureDesc);

            RootSignatureDescription1 missRootSignatureDesc = new(RootSignatureFlags.LocalRootSignature);
            _missRootSignature = Device.CreateRootSignature(missRootSignatureDesc);

            // Create the shaders
            ReadOnlyMemory<byte> raytracingShader = ShaderLoader.LoadShaderBytecode("Shadow/RayTracing");

            // Create the pipeline
            StateSubObject rayGenLibrary = new(new DxilLibraryDescription(raytracingShader, new ExportDescription("RayGen")));
            StateSubObject hitLibrary = new(new DxilLibraryDescription(raytracingShader, new ExportDescription("ClosestHit")));
            StateSubObject missLibrary = new(new DxilLibraryDescription(raytracingShader, new ExportDescription("Miss")));

            StateSubObject hitGroup = new(new HitGroupDescription("HitGroup", HitGroupType.Triangles, closestHitShaderImport: "ClosestHit"));

            StateSubObject raytracingShaderConfig = new(new RaytracingShaderConfig(0, 0));

            StateSubObject shaderPayloadAssociation = new(new SubObjectToExportsAssociation(raytracingShaderConfig, "RayGen", "ClosestHit", "Miss"));

            StateSubObject rayGenRootSignatureStateObject = new(new LocalRootSignature(_rayGenRootSignature));
            StateSubObject rayGenRootSignatureAssociation = new(new SubObjectToExportsAssociation(rayGenRootSignatureStateObject, "RayGen"));

            StateSubObject hitRootSignatureStateObject = new(new LocalRootSignature(_hitRootSignature));
            StateSubObject hitRootSignatureAssociation = new(new SubObjectToExportsAssociation(hitRootSignatureStateObject, "ClosestHit"));

            StateSubObject missRootSignatureStateObject = new(new LocalRootSignature(_missRootSignature));
            StateSubObject missRootSignatureAssociation = new(new SubObjectToExportsAssociation(missRootSignatureStateObject, "Miss"));

            StateSubObject raytracingPipelineConfig = new(new RaytracingPipelineConfig(1));

            StateSubObject globalRootSignatureStateObject = new(new GlobalRootSignature(_raytracingRootSignature));

            StateSubObject[] stateSubObjects =
            [
                rayGenLibrary,
                hitLibrary,
                missLibrary,

                hitGroup,

                raytracingShaderConfig,
                shaderPayloadAssociation,

                rayGenRootSignatureStateObject,
                rayGenRootSignatureAssociation,
                hitRootSignatureStateObject,
                hitRootSignatureAssociation,
                missRootSignatureStateObject,
                missRootSignatureAssociation,

                raytracingPipelineConfig,

                globalRootSignatureStateObject,
            ];

            _raytracingStateObject = Device.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, stateSubObjects));

            Log.LogInfo("Created raytracing state object");
        }

        // Build acceleration structures needed for raytracing
        if (RayTracingSupported)
        {
            _commandList.Reset(_commandAllocators[_frameIndex]);

            RaytracingGeometryDescription geometryDescription = new()
            {
                Triangles = new RaytracingGeometryTrianglesDescription(
                        new GpuVirtualAddressAndStride(_vertexBuffer.GPUVirtualAddress, (ulong)Unsafe.SizeOf<TriangleVertex>()), Format.R32G32B32_Float, 3,
                        indexBuffer: _indexBuffer.GPUVirtualAddress, indexFormat: Format.R32_UInt, indexCount: (uint)_indexCount),
                Flags = RaytracingGeometryFlags.Opaque,
            };

            BuildRaytracingAccelerationStructureInputs bottomLevelInputs = new()
            {
                Type = RaytracingAccelerationStructureType.BottomLevel,
                Flags = RaytracingAccelerationStructureBuildFlags.None,
                Layout = ElementsLayout.Array,
                DescriptorsCount = 1,
                GeometryDescriptions = [geometryDescription],
            };

            RaytracingAccelerationStructurePrebuildInfo bottomLevelInfo = Device.GetRaytracingAccelerationStructurePrebuildInfo(bottomLevelInputs);

            if (bottomLevelInfo.ResultDataMaxSizeInBytes == 0)
            {
                Log.LogCrit("Failed to create bottom level inputs");
                throw new Exception();
            }

            BuildRaytracingAccelerationStructureInputs topLevelInputs = new()
            {
                Type = RaytracingAccelerationStructureType.TopLevel,
                Flags = RaytracingAccelerationStructureBuildFlags.None,
                Layout = ElementsLayout.Array,
                DescriptorsCount = 1,
            };

            RaytracingAccelerationStructurePrebuildInfo topLevelInfo = Device.GetRaytracingAccelerationStructurePrebuildInfo(topLevelInputs);

            if (topLevelInfo.ResultDataMaxSizeInBytes == 0)
            {
                Log.LogCrit("Failed to create top level inputs");
                throw new Exception();
            }

            using ID3D12Resource scratchResource = Device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Buffer(Math.Max(topLevelInfo.ScratchDataSizeInBytes, bottomLevelInfo.ScratchDataSizeInBytes), ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess
                );

            _bottomLevelAccelerationStructure = Device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Buffer(Math.Max(topLevelInfo.ScratchDataSizeInBytes, bottomLevelInfo.ResultDataMaxSizeInBytes), ResourceFlags.AllowUnorderedAccess),
                ResourceStates.RaytracingAccelerationStructure
                );

            _bottomLevelAccelerationStructure.Name = nameof(_bottomLevelAccelerationStructure);

            _topLevelAccelerationStructure = Device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Buffer(Math.Max(topLevelInfo.ScratchDataSizeInBytes, bottomLevelInfo.ResultDataMaxSizeInBytes), ResourceFlags.AllowUnorderedAccess),
                ResourceStates.RaytracingAccelerationStructure
                );

            _topLevelAccelerationStructure.Name = nameof(_topLevelAccelerationStructure);


            // Create the instance buffer
            RaytracingInstanceDescription instanceDescription = new()
            {
                Transform = new Matrix3x4(1, 0, 0, 0,
                                          0, 1, 0, 0,
                                          0, 0, 1, 0),
                InstanceMask = 0xFF,
                InstanceID = (UInt24)0,
                Flags = RaytracingInstanceFlags.None,
                InstanceContributionToHitGroupIndex = (UInt24)0,
                AccelerationStructure = _bottomLevelAccelerationStructure.GPUVirtualAddress,
            };

            _instanceBuffer = Device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer((ulong)sizeof(RaytracingInstanceDescription)),
                ResourceStates.GenericRead);

            _instanceBuffer.SetData(instanceDescription);

            // Build the acceleration structures
            _commandList.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
            {
                Inputs = bottomLevelInputs,
                ScratchAccelerationStructureData = scratchResource.GPUVirtualAddress,
                DestinationAccelerationStructureData = _bottomLevelAccelerationStructure.GPUVirtualAddress,
            });

            _commandList.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_bottomLevelAccelerationStructure)));

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
        }

        if (RayTracingSupported)
        {
            uint cbufferSize = (uint)(Unsafe.SizeOf<RaytracingConstants>() * SwapChainBufferCount);
            _raytracingConstantBuffer = Device.CreateCommittedResource(
                    HeapType.Upload,
                    ResourceDescription.Buffer(cbufferSize),
                    ResourceStates.GenericRead);

            fixed (void* pMemory = &_raytracingConstantsMemory)
            {
                _raytracingConstantBuffer.Map(0, pMemory);
            }

            _raytracingResourceHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, DescriptorHeapFlags.ShaderVisible));

            CreateShadowRaytracingResources(out _raytracedShadowMask);
        }

        if (RayTracingSupported)
        {
            static uint Align(uint value, uint alignment)
            {
                return ((value + alignment - 1) / alignment) * alignment;
            }

            _raytracingStateObjectProperties = _raytracingStateObject!.QueryInterface<ID3D12StateObjectProperties>();

            _shaderBindingTableEntrySize = D3D12.ShaderIdentifierSizeInBytes;
            _shaderBindingTableEntrySize += 8; // Ray generator descriptor table
            _shaderBindingTableEntrySize = Align(_shaderBindingTableEntrySize, D3D12.RaytracingShaderRecordByteAlignment);

            ulong shaderBindingTableSize = _shaderBindingTableEntrySize * 3;

            _shaderBindingTableBuffer = Device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(shaderBindingTableSize),
                ResourceStates.GenericRead);

            byte* shaderBindingTableBufferDataPointer;
            _shaderBindingTableBuffer.Map(0, &shaderBindingTableBufferDataPointer).CheckError();

            unsafe
            {
                Unsafe.CopyBlockUnaligned((void*)shaderBindingTableBufferDataPointer, (void*)_raytracingStateObjectProperties.GetShaderIdentifier("RayGen"), D3D12.ShaderIdentifierSizeInBytes);

                *(GpuDescriptorHandle*)(shaderBindingTableBufferDataPointer + _shaderBindingTableEntrySize * 0 + D3D12.ShaderIdentifierSizeInBytes) = _raytracingResourceHeap!.GetGPUDescriptorHandleForHeapStart1();

                Unsafe.CopyBlockUnaligned(shaderBindingTableBufferDataPointer + _shaderBindingTableEntrySize * 1, (void*)_raytracingStateObjectProperties.GetShaderIdentifier("HitGroup"), D3D12.ShaderIdentifierSizeInBytes);

                *(ulong*)(shaderBindingTableBufferDataPointer + _shaderBindingTableEntrySize * 1 + D3D12.ShaderIdentifierSizeInBytes) = _vertexBuffer.GPUVirtualAddress;

                Unsafe.CopyBlockUnaligned(shaderBindingTableBufferDataPointer + _shaderBindingTableEntrySize * 2, (void*)_raytracingStateObjectProperties.GetShaderIdentifier("Miss"), D3D12.ShaderIdentifierSizeInBytes);
            }

            _shaderBindingTableBuffer.Unmap(0);

            Log.LogInfo("Created raytracing shader binding table");
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
    private int _raytracedShadowMaskImGuiViewId;

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
            // Forces greyscale, maps xyzw => xxxx
            Shader4ComponentMapping = ShaderComponentMapping.Encode(
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent0),
        };

        // + size * 1 because 1 is the index of the depth view, see the debug view header in imgui
        Device.CreateShaderResourceView(depthStencilTexture, debugSrvDesc, _debugResourceDescriptorHeap.GetCPUDescriptorHandleForHeapStart1() + (int)(_debugResourceDescriptorSize * 1));
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

    private void CreateShadowRaytracingResources(out ID3D12Resource raytracedShadowMask)
    {
        if (!RayTracingSupported)
        {
            throw new InvalidOperationException();
        }

        ResourceDescription outputBufferDescription = new()
        {
            DepthOrArraySize = 1,
            Dimension = ResourceDimension.Texture2D,
            Format = Format.R32_Float,
            Flags = ResourceFlags.AllowUnorderedAccess,
            Width = (ulong)Window.Size.X,
            Height = (uint)Window.Size.Y,
            Layout = TextureLayout.Unknown,
            MipLevels = 1,
            SampleDescription = new SampleDescription(1, 0),
        };

        raytracedShadowMask = Device.CreateCommittedResource(
            HeapType.Default,
            outputBufferDescription,
            ResourceStates.CopySource);

        uint raytracingResourceHeapSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        CpuDescriptorHandle heapHandle = _raytracingResourceHeap!.GetCPUDescriptorHandleForHeapStart1();

        UnorderedAccessViewDescription shadowmaskResourceViewDesc = new()
        {
            ViewDimension = UnorderedAccessViewDimension.Texture2D
        };
        Device.CreateUnorderedAccessView(_raytracedShadowMask, null, shadowmaskResourceViewDesc, heapHandle);
        heapHandle += (int)raytracingResourceHeapSize;

        // TODO: We don't need to build this every time, move to separate method probably
        ShaderResourceViewDescription accelerationStructureViewDescription = new()
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            RaytracingAccelerationStructure = new()
            {
                Location = _topLevelAccelerationStructure!.GPUVirtualAddress
            }
        };
        Device.CreateShaderResourceView(null, accelerationStructureViewDescription, heapHandle);
        heapHandle += (int)raytracingResourceHeapSize;

        ShaderResourceViewDescription srvDesc = new()
        {
            Format = _depthStencilFormat,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new()
            {
                MipLevels = 1,
                MostDetailedMip = 0,
            }
        };
        Device.CreateShaderResourceView(_depthStencilTexture, srvDesc, heapHandle);
        heapHandle += (int)raytracingResourceHeapSize;

        Log.LogInfo("Created raytracing resources");

        ShaderResourceViewDescription debugSrvDesc = new()
        {
            Format = outputBufferDescription.Format,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new()
            {
                MipLevels = 1,
                MostDetailedMip = 0,
            },
            // Forces greyscale, maps xyzw => xxxx
            Shader4ComponentMapping = ShaderComponentMapping.Encode(
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent0,
                ShaderComponentMappingSource.FromMemoryComponent3),
        };

        _raytracedShadowMaskImGuiViewId = _imGuiRenderer.BindTextureView(raytracedShadowMask, debugSrvDesc);
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
            _imGuiRenderer.UnbindTextureView(_raytracedShadowMaskImGuiViewId);
            _raytracedShadowMask!.Dispose();
        }

        Log.LogInfo("Resizing swapchain buffers");
        SwapChain.ResizeBuffers1(SwapChainBufferCount, (uint)width, (uint)height, Format.R8G8B8A8_UNorm);
        CreateFrameResources(out _renderTargets);
        CreateDepthStencil(out _depthStencilTexture, out _depthStencilFormat);

        if (RayTracingSupported)
        {
            CreateShadowRaytracingResources(out _raytracedShadowMask);
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

    private int _debugRenderViewIndex = 0;

    private float _depthDebugViewSize = 512f;

    private float _shadowDebugViewSize = 512f;

    private void DrawImGui()
    {
        if (ImGui.Begin("Debug Window"))
        {
            ImGui.Text($"FPS: {ImGui.GetIO().Framerate}");
            if (ImGui.CollapsingHeader("Fullscreen Debug Views"))
            {
                string[] debugViewNames = ["None", "Depth Buffer"];
                ImGui.Combo("Texture Debug View", ref _debugRenderViewIndex, debugViewNames, debugViewNames.Length);
            }

            if (ImGui.CollapsingHeader("Image Views"))
            {
                ImGui.SliderFloat("Depth Texture View Size", ref _depthDebugViewSize, 32f, 4096f);
                ImGui.Image(_depthDebugImGuiViewId, Vector2.Normalize(Window.Size) * _depthDebugViewSize);

                ImGui.SliderFloat("Shadow Texture View Size", ref _shadowDebugViewSize, 32f, 4096f);
                ImGui.Image(_raytracedShadowMaskImGuiViewId, Vector2.Normalize(Window.Size) * _shadowDebugViewSize);
            }
        }
        ImGui.End();
    }

    private void DrawFrame()
    {
        // Create the projection matrix and then copy it to the mapped part of memory for the current frameIndex
        // as mentioned before, every frame that can be in flight gets its own constant buffer
        //
        // This could maybe be simplified so that there's only one constant buffer, but idk; the directx samples do it like this
        {
            Constants constants = new(_mainCamera.ViewMatrix * _mainCamera.ProjectionMatrix);
            void* dest = _constantsMemory + Unsafe.SizeOf<Constants>() * _frameIndex;
            Unsafe.CopyBlock(dest, &constants, (uint)Unsafe.SizeOf<Constants>());
        }

        if (RayTracingSupported)
        {
            if (Matrix4x4.Invert(_mainCamera.ViewMatrix * _mainCamera.ProjectionMatrix, out Matrix4x4 inverseViewProjection))
            {
                RaytracingConstants raytracingConstants = new(inverseViewProjection);
                void* dest = _raytracingConstantsMemory + Unsafe.SizeOf<RaytracingConstants>() * _frameIndex;
                Unsafe.CopyBlock(dest, &raytracingConstants, (uint)Unsafe.SizeOf<RaytracingConstants>());
            }
        }

        _commandAllocators[_frameIndex].Reset();
        _commandList.Reset(_commandAllocators[_frameIndex], _graphicsPipelineState);

        // Indicate that the back buffer will be used as a render target.
        _commandList.ResourceBarrierTransition(_renderTargets[_frameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

        CpuDescriptorHandle rtvDescriptor = new(_rtvDescriptorHeap.GetCPUDescriptorHandleForHeapStart1(), (int)_frameIndex, _rtvDescriptorSize);
        CpuDescriptorHandle dsvDescriptor = _dsvDescriptorHeap.GetCPUDescriptorHandleForHeapStart1();

        _commandList.SetGraphicsRootSignature(_graphicsRootSignature);

        _commandList.SetMarker("Depth Prepass");
        {
            _commandList.OMSetRenderTargets(null!, dsvDescriptor);
            _commandList.ClearDepthStencilView(dsvDescriptor, ClearFlags.Depth, 1.0f, 0);


            // We directly set the constant buffer view to the current _constantBuffer[frameIndex]
            _commandList.SetGraphicsRootConstantBufferView(0, _constantBuffer.GPUVirtualAddress + (ulong)(_frameIndex * Unsafe.SizeOf<Constants>()));

            _commandList.RSSetViewport(new Viewport(Window.Size.X, Window.Size.Y));
            _commandList.RSSetScissorRect((int)Window.Size.X, (int)Window.Size.Y);

            _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _commandList.IASetVertexBuffers(0, _vertexBufferView);
            _commandList.IASetIndexBuffer(_indexBufferView);
            _commandList.DrawIndexedInstanced((uint)_indexCount, 1, 0, 0, 0);
        }

        _commandList.SetMarker("Ray Tracing");
        if (RayTracingSupported)
        {
            _commandList.SetComputeRootSignature(_raytracingRootSignature);
            _commandList.SetDescriptorHeaps(_raytracingResourceHeap);
            _commandList.SetPipelineState1(_raytracingStateObject);

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

            _commandList.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_raytracedShadowMask)));
        }

        _commandList.SetMarker("Triangle Frame");
        {
            _commandList.SetPipelineState(_graphicsPipelineState);

            Color4 clearColor = Colors.CornflowerBlue;

            _commandList.OMSetRenderTargets(rtvDescriptor, dsvDescriptor);
            _commandList.ClearRenderTargetView(rtvDescriptor, clearColor);

            // We directly set the constant buffer view to the current _constantBuffer[frameIndex]
            _commandList.SetGraphicsRootConstantBufferView(0, _constantBuffer.GPUVirtualAddress + (ulong)(_frameIndex * Unsafe.SizeOf<Constants>()));

            _commandList.RSSetViewport(new Viewport(Window.Size.X, Window.Size.Y));
            _commandList.RSSetScissorRect((int)Window.Size.X, (int)Window.Size.Y);

            _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _commandList.IASetVertexBuffers(0, _vertexBufferView);
            _commandList.IASetIndexBuffer(_indexBufferView);
            _commandList.DrawIndexedInstanced((uint)_indexCount, 1, 0, 0, 0);
        }

        if (_debugRenderViewIndex > 0)
        {
            _commandList.OMSetRenderTargets(rtvDescriptor, null);

            _commandList.SetMarker("Debug");

            switch (_debugRenderViewIndex)
            {
                case 1:
                    _commandList.SetPipelineState(_depthDebugPipelineState);
                    break;
                default:
                    throw new InvalidOperationException($"No pipeline state for debug render view index {_debugRenderViewIndex}");
            }

            _commandList.SetGraphicsRootSignature(_debugRootSignature);

            _commandList.SetDescriptorHeaps(_debugResourceDescriptorHeap);
            _commandList.SetGraphicsRootDescriptorTable(1, _debugResourceDescriptorHeap.GetGPUDescriptorHandleForHeapStart1() + (int)(_debugRenderViewIndex * _debugResourceDescriptorSize));

            _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _commandList.DrawInstanced(3, 1, 0, 0);
        }

        ImGui.Render();

        _commandList.OMSetRenderTargets(rtvDescriptor, null);
        _commandList.SetMarker("ImGui");
        _imGuiRenderer.PopulateCommandList(_commandList, _frameIndex, ImGui.GetDrawData());

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

            _frameFenceEvent.Dispose();

            CopyManager.Dispose();
            _imGuiRenderer.Dispose();

            _depthDebugPipelineState.Dispose();
            _debugRootSignature.Dispose();
            _debugResourceDescriptorHeap.Dispose();

            if (RayTracingSupported)
            {
                _raytracingRootSignature?.Dispose();
                _raytracedShadowMask?.Dispose();
                _raytracingStateObject?.Dispose();
                _raytracingResourceHeap?.Dispose();
                _raytracingConstantBuffer?.Dispose();
                _raytracingStateObjectProperties?.Dispose();
                _rayGenRootSignature?.Dispose();
                _hitRootSignature?.Dispose();
                _missRootSignature?.Dispose();
                _topLevelAccelerationStructure?.Dispose();
                _bottomLevelAccelerationStructure?.Dispose();
                _instanceBuffer?.Dispose();
                _shaderBindingTableBuffer?.Dispose();
            }

            _constantBuffer.Dispose();
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
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
    private record struct TriangleVertex(Vector3 position, Vector3 normal);

    [StructLayout(LayoutKind.Sequential)]
    private record struct Constants(Matrix4x4 ViewProjectionMatrix);

    [StructLayout(LayoutKind.Sequential)]
    private record struct RaytracingConstants(Matrix4x4 InverseViewProjection);
}
using System.Runtime.CompilerServices;
using MiniEngine.Logging;
using Vortice.Direct3D12;

namespace MiniEngine.Graphics;

public unsafe partial class D3D12Renderer
{
    private readonly ID3D12RootSignature? _raytracingRootSignature;
    private readonly ID3D12RootSignature? _rayGenRootSignature;
    private readonly ID3D12RootSignature? _hitRootSignature;
    private readonly ID3D12RootSignature? _missRootSignature;
    private readonly ID3D12StateObject? _raytracingStateObject;

    private readonly ID3D12StateObjectProperties? _raytracingStateObjectProperties;
    private readonly uint _shaderBindingTableEntrySize;
    private readonly ID3D12Resource? _shaderBindingTableBuffer;

    private readonly ID3D12Resource? _instanceBuffer;
    private readonly ID3D12Resource? _topLevelAccelerationStructure;

    private readonly ID3D12Resource? _raytracingConstantBuffer;
    private byte* _raytracingConstantsMemory = null;

    private void CreateShadowRayTracingState(out ID3D12RootSignature raytracingRootSignature, out ID3D12RootSignature rayGenRootSignature, out ID3D12RootSignature hitRootSignature, out ID3D12RootSignature missRootSignature, out ID3D12StateObject raytracingStateObject)
    {
        RootSignatureDescription1 raytracingRootSignatureDesc = new(RootSignatureFlags.None)
        {
            Parameters =
            [
                new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic), ShaderVisibility.All),
            ]
        };

        raytracingRootSignature = Device.CreateRootSignature(raytracingRootSignatureDesc);

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

        rayGenRootSignature = Device.CreateRootSignature(rayGenSignatureDesc);

        RootSignatureDescription1 hitRootSignatureDesc = new(RootSignatureFlags.LocalRootSignature);
        hitRootSignature = Device.CreateRootSignature(hitRootSignatureDesc);

        RootSignatureDescription1 missRootSignatureDesc = new(RootSignatureFlags.LocalRootSignature);
        missRootSignature = Device.CreateRootSignature(missRootSignatureDesc);

        // Create the shaders
        ReadOnlyMemory<byte> raytracingShader = ShaderLoader.LoadShaderBytecode("Shadow/RayTracing");

        // Create the pipeline
        StateSubObject rayGenLibrary = new(new DxilLibraryDescription(raytracingShader, new ExportDescription("RayGen")));
        StateSubObject hitLibrary = new(new DxilLibraryDescription(raytracingShader, new ExportDescription("ClosestHit")));
        StateSubObject missLibrary = new(new DxilLibraryDescription(raytracingShader, new ExportDescription("Miss")));

        StateSubObject hitGroup = new(new HitGroupDescription("HitGroup", HitGroupType.Triangles, closestHitShaderImport: "ClosestHit"));

        StateSubObject raytracingShaderConfig = new(new RaytracingShaderConfig(0, 0));

        StateSubObject shaderPayloadAssociation = new(new SubObjectToExportsAssociation(raytracingShaderConfig, "RayGen", "ClosestHit", "Miss"));

        StateSubObject rayGenRootSignatureStateObject = new(new LocalRootSignature(rayGenRootSignature));
        StateSubObject rayGenRootSignatureAssociation = new(new SubObjectToExportsAssociation(rayGenRootSignatureStateObject, "RayGen"));

        StateSubObject hitRootSignatureStateObject = new(new LocalRootSignature(hitRootSignature));
        StateSubObject hitRootSignatureAssociation = new(new SubObjectToExportsAssociation(hitRootSignatureStateObject, "ClosestHit"));

        StateSubObject missRootSignatureStateObject = new(new LocalRootSignature(missRootSignature));
        StateSubObject missRootSignatureAssociation = new(new SubObjectToExportsAssociation(missRootSignatureStateObject, "Miss"));

        StateSubObject raytracingPipelineConfig = new(new RaytracingPipelineConfig(1));

        StateSubObject globalRootSignatureStateObject = new(new GlobalRootSignature(raytracingRootSignature));

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

        raytracingStateObject = Device.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, stateSubObjects));

        Log.LogInfo("Created raytracing state object");
    }

    private void CreateShadowRayTracingShaderBindingTable(out ID3D12StateObjectProperties raytracingStateObjectProperties, out uint shaderBindingTableEntrySize, out ID3D12Resource shaderBindingTableBuffer)
    {
        static uint Align(uint value, uint alignment)
        {
            return ((value + alignment - 1) / alignment) * alignment;
        }

        raytracingStateObjectProperties = _raytracingStateObject!.QueryInterface<ID3D12StateObjectProperties>();

        shaderBindingTableEntrySize = D3D12.ShaderIdentifierSizeInBytes;
        shaderBindingTableEntrySize += 8; // uhh ??
        shaderBindingTableEntrySize = Align(shaderBindingTableEntrySize, D3D12.RaytracingShaderRecordByteAlignment);

        ulong shaderBindingTableSize = _shaderBindingTableEntrySize * 3;

        shaderBindingTableBuffer = Device.CreateCommittedResource(
            HeapType.Upload,
            ResourceDescription.Buffer(shaderBindingTableSize),
            ResourceStates.GenericRead);

        byte* shaderBindingTableBufferDataPointer;
        shaderBindingTableBuffer.Map(0, &shaderBindingTableBufferDataPointer);

        unsafe
        {
            Unsafe.CopyBlockUnaligned((void*)shaderBindingTableBufferDataPointer, (void*)raytracingStateObjectProperties.GetShaderIdentifier("RayGen"), D3D12.ShaderIdentifierSizeInBytes);

            //*(GpuDescriptorHandle*)(shaderBindingTableBufferDataPointer + _shaderBindingTableEntrySize * 0 + D3D12.ShaderIdentifierSizeInBytes) = _raytracingResourceHeap!.GetGPUDescriptorHandleForHeapStart1();

            Unsafe.CopyBlockUnaligned(shaderBindingTableBufferDataPointer + shaderBindingTableEntrySize * 1, (void*)raytracingStateObjectProperties.GetShaderIdentifier("HitGroup"), D3D12.ShaderIdentifierSizeInBytes);

            //*(ulong*)(shaderBindingTableBufferDataPointer + _shaderBindingTableEntrySize * 1 + D3D12.ShaderIdentifierSizeInBytes) = _vertexBuffer.GPUVirtualAddress;

            Unsafe.CopyBlockUnaligned(shaderBindingTableBufferDataPointer + shaderBindingTableEntrySize * 2, (void*)raytracingStateObjectProperties.GetShaderIdentifier("Miss"), D3D12.ShaderIdentifierSizeInBytes);
        }

        shaderBindingTableBuffer.Unmap(0);

        Log.LogInfo("Created raytracing shader binding table");
    }
}
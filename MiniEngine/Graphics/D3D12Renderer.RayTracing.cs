using Vortice.Direct3D12;

namespace MiniEngine.Graphics;

public unsafe partial class D3D12Renderer
{
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
}
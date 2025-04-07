using Vortice.Direct3D12;

namespace MiniEngine.Graphics;

public interface IShadowRenderable
{
    RaytracingAccelerationStructurePrebuildInfo BottomLevelPrebuildInfo { get; }
    RaytracingInstanceDescription InstanceDescription { get; }

    void BuildBottomLevelAccelerationStructure(ID3D12GraphicsCommandList4 commandList, ID3D12Resource scratchResource);
}
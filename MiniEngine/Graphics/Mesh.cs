using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MiniEngine.Logging;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MiniEngine.Graphics;

public class Mesh : IDisposable, IRenderable, IShadowRenderable
{
    private bool _disposed;

    private readonly ID3D12Resource _vertexBuffer;
    private readonly VertexBufferView _vertexBufferView;

    private readonly ID3D12Resource _indexBuffer;
    private readonly IndexBufferView _indexBufferView;

    private readonly List<MeshPrimitiveData> _primitives;


    private readonly BuildRaytracingAccelerationStructureInputs? _bottomLevelInputs;
    public RaytracingAccelerationStructurePrebuildInfo BottomLevelPrebuildInfo { get; }

    private readonly ID3D12Resource? _bottomLevelAccelerationStructure;

    public RaytracingInstanceDescription InstanceDescription { get; }

    public Mesh(ID3D12Device device, List<MeshPrimitiveData> primitives, ReadOnlySpan<MeshVertex> verts, ReadOnlySpan<int> idxs)
    {
        _primitives = primitives;

        uint vertexBufferStride = (uint)Unsafe.SizeOf<MeshVertex>();
        uint vertexBufferSize = (uint)(verts.Length * vertexBufferStride);

        _vertexBuffer = device.CreateCommittedResource(
            HeapType.Upload,
            ResourceDescription.Buffer(vertexBufferSize),
            ResourceStates.GenericRead);

        uint indexBufferStride = (uint)Unsafe.SizeOf<int>();
        uint indexBufferSize = (uint)(idxs.Length * indexBufferStride);

        _indexBuffer = device.CreateCommittedResource(
            HeapType.Upload,
            ResourceDescription.Buffer(indexBufferSize),
            ResourceStates.GenericRead);

        _vertexBuffer.SetData(verts);
        _vertexBufferView = new VertexBufferView(_vertexBuffer.GPUVirtualAddress, vertexBufferSize, vertexBufferStride);

        _indexBuffer.SetData(idxs);
        _indexBufferView = new IndexBufferView(_indexBuffer.GPUVirtualAddress, indexBufferSize, Format.R32_UInt);

        if (D3D12Renderer.RayTracingSupported)
        {
            List<RaytracingGeometryDescription> geometryDescriptions = [];

            foreach (MeshPrimitiveData meshPrimitive in _primitives)
            {
                geometryDescriptions.Add(new()
                {
                    Triangles = new RaytracingGeometryTrianglesDescription(
                            new GpuVirtualAddressAndStride(_vertexBuffer.GPUVirtualAddress + (ulong)(Unsafe.SizeOf<MeshVertex>() * meshPrimitive.BaseVertex), (ulong)Unsafe.SizeOf<MeshVertex>()), Format.R32G32B32_Float, 3,
                            indexBuffer: _indexBuffer.GPUVirtualAddress + (ulong)(Unsafe.SizeOf<int>() * meshPrimitive.IndexStart), indexFormat: Format.R32_UInt, indexCount: (uint)meshPrimitive.IndexCount),
                    Flags = RaytracingGeometryFlags.Opaque
                });
            }

            _bottomLevelInputs = new()
            {
                Type = RaytracingAccelerationStructureType.BottomLevel,
                Flags = RaytracingAccelerationStructureBuildFlags.None,
                Layout = ElementsLayout.Array,
                DescriptorsCount = (uint)geometryDescriptions.Count,
                GeometryDescriptions = [.. geometryDescriptions],
            };

            using ID3D12Device5 device5 = device.QueryInterface<ID3D12Device5>();

            BottomLevelPrebuildInfo = device5.GetRaytracingAccelerationStructurePrebuildInfo(_bottomLevelInputs);

            if (BottomLevelPrebuildInfo.ResultDataMaxSizeInBytes == 0)
            {
                Log.LogCrit("Failed to create bottom level inputs");
                throw new Exception();
            }

            _bottomLevelAccelerationStructure = device.CreateCommittedResource(
                    HeapType.Default,
                    ResourceDescription.Buffer(BottomLevelPrebuildInfo.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess),
                    ResourceStates.RaytracingAccelerationStructure);

            InstanceDescription = new()
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
        }
    }

    public void Render(ID3D12GraphicsCommandList4 commandList)
    {
        commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commandList.IASetVertexBuffers(0, _vertexBufferView);
        commandList.IASetIndexBuffer(_indexBufferView);

        foreach (MeshPrimitiveData meshPrimitive in _primitives)
        {
            commandList.DrawIndexedInstanced((uint)meshPrimitive.IndexCount, 1, (uint)meshPrimitive.IndexStart, meshPrimitive.BaseVertex, 0);
        }
    }

    public void RenderDepth(ID3D12GraphicsCommandList4 commandList)
    {
        commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commandList.IASetVertexBuffers(0, _vertexBufferView);
        commandList.IASetIndexBuffer(_indexBufferView);

        foreach (MeshPrimitiveData meshPrimitive in _primitives)
        {
            commandList.DrawIndexedInstanced((uint)meshPrimitive.IndexCount, 1, (uint)meshPrimitive.IndexStart, meshPrimitive.BaseVertex, 0);
        }
    }

    public void BuildBottomLevelAccelerationStructure(ID3D12GraphicsCommandList4 commandList, ID3D12Resource scratchResource)
    {
        commandList.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
        {
            Inputs = _bottomLevelInputs,
            ScratchAccelerationStructureData = scratchResource.GPUVirtualAddress,
            DestinationAccelerationStructureData = _bottomLevelAccelerationStructure!.GPUVirtualAddress,
        });

        commandList.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_bottomLevelAccelerationStructure)));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _bottomLevelAccelerationStructure?.Dispose();

            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public record struct MeshPrimitiveData(int BaseVertex, int IndexStart, int IndexCount);

    [StructLayout(LayoutKind.Sequential)]
    public record struct MeshVertex(Vector3 position, Vector3 normal);
}
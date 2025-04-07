using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace MiniEngine.Graphics;

public class Mesh : IDisposable, IRenderable, IShadowRenderable
{
    private bool _disposed;

    private readonly ID3D12Resource _vertexBuffer;
    private readonly VertexBufferView _vertexBufferView;

    private readonly ID3D12Resource _indexBuffer;
    private readonly IndexBufferView _indexBufferView;

    private readonly List<MeshPrimitiveData> _primitives;

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

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
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
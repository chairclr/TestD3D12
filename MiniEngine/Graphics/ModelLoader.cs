using System.Numerics;
using System.Runtime.InteropServices;
using glTFLoader;
using glTFLoader.Schema;
using MiniEngine.Logging;
using Vortice.Direct3D12;
using GltfMesh = glTFLoader.Schema.Mesh;
using GltfNode = glTFLoader.Schema.Node;

namespace MiniEngine.Graphics;

public class ModelLoader : IDisposable
{
    private readonly ID3D12Device _device;

    private bool _disposed;

    public ModelLoader(ID3D12Device device)
    {
        _device = device;

        _device.AddRef();
    }

    public List<Mesh> LoadModelMeshes(string modelPath)
    {
        Gltf model = Interface.LoadModel(modelPath);
        Span<byte> modelData = Interface.LoadBinaryBuffer(modelPath);

        List<Mesh> meshes = [];

        // Multi-scene support?
        Scene scene = model.Scenes[model.Scene ?? 0];

        foreach (int nodeIndex in scene.Nodes)
        {
            GltfNode node = model.Nodes[nodeIndex];

            if (!node.Mesh.HasValue)
            {
                continue;
            }

            GltfMesh mesh = model.Meshes[node.Mesh.Value];

            int totalVerts = mesh.Primitives.Sum(x => model.Accessors[x.Attributes["POSITION"]].Count);
            int totalIndicies = mesh.Primitives.Sum(x => x.Indices.HasValue ? model.Accessors[x.Indices.Value].Count : 0);

            Span<Mesh.MeshVertex> verts = new Mesh.MeshVertex[totalVerts];
            Span<int> idxs = new int[totalIndicies];

            List<Mesh.MeshPrimitiveData> primitives = CopyMeshPrimitivesToModelMeshes(model, modelData, mesh, verts, idxs);

            meshes.Add(new(_device, primitives, verts, idxs));
        }

        return meshes;
    }

    private List<Mesh.MeshPrimitiveData> CopyMeshPrimitivesToModelMeshes(Gltf model, ReadOnlySpan<byte> modelData, GltfMesh mesh, Span<Mesh.MeshVertex> verts, Span<int> idxs)
    {
        List<Mesh.MeshPrimitiveData> primitives = [];

        int currentVertexPos = 0;
        int currentIndexPos = 0;

        foreach (MeshPrimitive primitive in mesh.Primitives)
        {
            if (primitive.Mode != MeshPrimitive.ModeEnum.TRIANGLES)
            {
                throw new NotImplementedException();
            }

            Accessor posAccessor = model.Accessors[primitive.Attributes["POSITION"]];
            BufferView posView = model.BufferViews[posAccessor.BufferView!.Value];
            int posOffset = posView.ByteOffset + posAccessor.ByteOffset;
            int posStride = posView.ByteStride ?? 12;
            if (posStride != 12)
            {
                throw new NotImplementedException();
            }

            Accessor normalAccessor = model.Accessors[primitive.Attributes["NORMAL"]];
            BufferView normalView = model.BufferViews[normalAccessor.BufferView!.Value];
            int normalOffset = normalView.ByteOffset + normalAccessor.ByteOffset;
            int normalStride = normalView.ByteStride ?? 12;
            if (normalStride != 12)
            {
                throw new NotImplementedException();
            }

            Accessor idxAccessor = model.Accessors[primitive.Indices!.Value];
            BufferView idxView = model.BufferViews[idxAccessor.BufferView!.Value];
            int idxOffset = idxView.ByteOffset + idxAccessor.ByteOffset;
            int idxStride = idxView.ByteStride ?? 4;
            if (idxStride != 4)
            {
                throw new NotImplementedException();
            }

            ReadOnlySpan<Vector3> posData = MemoryMarshal.Cast<byte, Vector3>(modelData[posOffset..(posOffset + (posStride * posAccessor.Count))]);
            ReadOnlySpan<Vector3> normalData = MemoryMarshal.Cast<byte, Vector3>(modelData[normalOffset..(normalOffset + (normalStride * normalAccessor.Count))]);
            ReadOnlySpan<int> idxData = MemoryMarshal.Cast<byte, int>(modelData[idxOffset..(idxOffset + (idxStride * idxAccessor.Count))]);

            for (int i = 0; i < posData.Length; i++)
            {
                verts[currentVertexPos + i] = new Mesh.MeshVertex(posData[i], normalData[i]);
            }

            idxData.CopyTo(idxs[currentIndexPos..]);

            primitives.Add(new(currentVertexPos, currentIndexPos, idxData.Length));

            currentVertexPos += posData.Length;
            currentIndexPos += idxData.Length;
        }

        return primitives;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            Log.LogInfo($"Disposing {nameof(ModelLoader)}");

            _device.Release();

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
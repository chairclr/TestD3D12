using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.VisualBasic;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using ImDrawIdx = ushort;

namespace TestD3D12.Graphics;

public unsafe class ImGuiRenderer : IDisposable
{
    private readonly ID3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipelineState;

    private readonly ID3D12DescriptorHeap _resourceDescriptorHeap;
    private readonly uint _resourceDescriptorSize;

    private ID3D12Resource? _vertexBuffer;
    private VertexBufferView? _vertexBufferView;
    private uint _vertexBufferSize = 2048;
    private ID3D12Resource? _indexBuffer;
    private IndexBufferView? _indexBufferView;
    private uint _indexBufferSize = 2048;

    private readonly ID3D12Resource _constantBuffer;

    private byte* _constantsMemory = null;

    //private readonly Dictionary<nint, id3d12resou> textureResources = new Dictionary<IntPtr, ID3D11ShaderResourceView>();

    private bool _disposed;

    public ImGuiRenderer(ID3D12Device device)
    {
        _device = device;

        _device.AddRef();

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

                // TODO: Textures
                //new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel),
            ],
            StaticSamplers =
            [
                new StaticSamplerDescription(SamplerDescription.LinearWrap, ShaderVisibility.Pixel, 0, 0),
            ]
        };

        _rootSignature = _device.CreateRootSignature(rootSignatureDesc);

        // ImGui needs two main shader resources:
        // Pixel shader:
        // 1. Texture (usually the font texture, but can be user defined)
        // 2. Texture sampler (LinearWrap)
        //
        // The vertex shader has a constant buffer, but we don't need to make a descriptor heap entry for it
        _resourceDescriptorHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible));
        _resourceDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        // We actually have a separate cbuffer for each swapchain buffer
        // Later we update only the cbuffer for the current frameIndex
        uint cbufferSize = (uint)(Unsafe.SizeOf<Constants>() * D3D12Renderer.SwapChainBufferCount);
        _constantBuffer = _device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(cbufferSize),
                ResourceStates.GenericRead);

        // Map the entire _constantBuffer, and d3d stores the pointer to that in _constantsMemory
        fixed (void* pMemory = &_constantsMemory)
        {
            _constantBuffer.Map(0, pMemory);
        }

        CpuDescriptorHandle resourceHandle = _resourceDescriptorHeap.GetCPUDescriptorHandleForHeapStart1();

        SamplerDescription samplerDesc = SamplerDescription.LinearWrap;
        _device.CreateSampler(ref samplerDesc, resourceHandle);
        resourceHandle += (int)_resourceDescriptorSize;

        // As defined in Assets/Shaders/Source/ImGui/ImGuiVS.hlsl
        InputElementDescription[] inputElementDescs =
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0),
        ];

        ReadOnlyMemory<byte> imguiVS = ShaderLoader.LoadShaderBytecode("ImGui/ImGuiVS");
        ReadOnlyMemory<byte> imguiPS = ShaderLoader.LoadShaderBytecode("ImGui/ImGuiPS");

        GraphicsPipelineStateDescription psoDesc = new()
        {
            RootSignature = _rootSignature,
            VertexShader = imguiVS,
            PixelShader = imguiPS,
            InputLayout = new InputLayoutDescription(inputElementDescs),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.AlphaBlend,
            // Disable depth and stencil
            DepthStencilState = DepthStencilDescription.Default with
            {
                DepthEnable = false,
                StencilEnable = false
            },
            RenderTargetFormats = [Format.R8G8B8A8_UNorm],
            SampleDescription = SampleDescription.Default,
        };

        _pipelineState = _device.CreateGraphicsPipelineState(psoDesc);

        // TODO: Set font texture
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out byte* _, out int _, out int _);
    }

    public void PopulateCommandList(ID3D12GraphicsCommandList4 commandList, uint frameIndex, ImDrawDataPtr data)
    {
        uint vertexBufferStride = (uint)Unsafe.SizeOf<ImDrawVert>();
        uint indexBufferStride = (uint)Unsafe.SizeOf<ImDrawIdx>();

        // Vertex buffer creation
        if (_vertexBuffer == null || _vertexBufferSize < data.TotalVtxCount)
        {
            _vertexBuffer?.Dispose();

            // Double the number of verts until we have enough
            while (_vertexBufferSize < data.TotalVtxCount)
            {
                _vertexBufferSize *= 2;
            }

            uint vertexBufferSizeBytes = _vertexBufferSize * vertexBufferStride;

            _vertexBuffer = _device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(vertexBufferSizeBytes),
                ResourceStates.GenericRead);

            // I guess this doesn't need to be disposed?
            _vertexBufferView = new VertexBufferView(_vertexBuffer.GPUVirtualAddress, vertexBufferSizeBytes, vertexBufferStride);
        }

        // Index buffer creation
        if (_indexBuffer == null || _indexBufferSize < data.TotalIdxCount)
        {
            _indexBuffer?.Dispose();

            // Double the number of indicies until we have enough
            while (_indexBufferSize < data.TotalIdxCount)
            {
                _indexBufferSize *= 2;
            }

            uint indexBufferSizeBytes = _indexBufferSize * indexBufferStride;

            _indexBuffer = _device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(indexBufferSizeBytes),
                ResourceStates.GenericRead);

            // I guess this doesn't need to be disposed?
            _indexBufferView = new IndexBufferView(_indexBuffer.GPUVirtualAddress, indexBufferSizeBytes, Format.R16_UInt);
        }

        // We only copy verts if there's any to draw
        if (data.TotalVtxCount > 0)
        {
            ImDrawVert* vertexResource = _vertexBuffer.Map<ImDrawVert>(0);
            ImDrawIdx* indexResource = _indexBuffer.Map<ImDrawIdx>(0);

            ImDrawVert* vertexDst = vertexResource;
            ImDrawIdx* indexDst = indexResource;
            for (int n = 0; n < data.CmdListsCount; n++)
            {
                ImDrawListPtr cmdList = data.CmdLists[n];

                uint vertexBytes = (uint)(cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>());
                uint indexBytes = (uint)(cmdList.IdxBuffer.Size * Unsafe.SizeOf<ImDrawIdx>());

                Unsafe.CopyBlock(vertexDst, (void*)cmdList.VtxBuffer.Data, vertexBytes);
                Unsafe.CopyBlock(indexDst, (void*)cmdList.IdxBuffer.Data, indexBytes);

                vertexDst += cmdList.VtxBuffer.Size;
                indexDst += cmdList.IdxBuffer.Size;
            }

            _vertexBuffer.Unmap(0, new Vortice.Direct3D12.Range(0, (nuint)(vertexDst - vertexResource)));
            _indexBuffer.Unmap(0, new Vortice.Direct3D12.Range(0, (nuint)(indexDst - indexResource)));
        }

        // Create the projection matrix and then copy it to the mapped part of memory for the current frameIndex
        // as mentioned before, every frame that can be in flight gets its own constant buffer
        //
        // This could maybe be simplified so that there's only one constant buffer, but idk; the directx samples do it like this
        Constants constants = new(Matrix4x4.CreateOrthographicOffCenter(0f, data.DisplaySize.X, data.DisplaySize.Y, 0.0f, -1.0f, 1.0f));
        void* dest = _constantsMemory + (Unsafe.SizeOf<Constants>() * frameIndex);
        Unsafe.CopyBlock(dest, &constants, (uint)Unsafe.SizeOf<Constants>());

        commandList.SetPipelineState(_pipelineState);
        commandList.SetGraphicsRootSignature(_rootSignature);

        // We directly set the constant buffer view to the current _constantBuffer[frameIndex]
        commandList.SetGraphicsRootConstantBufferView(0, _constantBuffer.GPUVirtualAddress + (ulong)(frameIndex * Unsafe.SizeOf<Constants>()));

        commandList.SetDescriptorHeaps(_resourceDescriptorHeap);

        commandList.IASetVertexBuffers(0, _vertexBufferView!.Value);
        commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commandList.IASetIndexBuffer(_indexBufferView!.Value);

        int global_idx_offset = 0;
        int global_vtx_offset = 0;
        Vector2 clip_off = data.DisplayPos;
        for (int n = 0; n < data.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = data.CmdLists[n];

            for (int i = 0; i < cmdList.CmdBuffer.Size; i++)
            {
                ImDrawCmdPtr cmd = cmdList.CmdBuffer[i];

                if (cmd.UserCallback != IntPtr.Zero)
                {
                    throw new NotImplementedException("User callbacks not implemented");
                }
                else
                {
                    Vector2 clip_min = new(cmd.ClipRect.X - clip_off.X, cmd.ClipRect.Y - clip_off.Y);
                    Vector2 clip_max = new(cmd.ClipRect.Z - clip_off.X, cmd.ClipRect.W - clip_off.Y);

                    // Skip rendering if the rect has a negative width or height
                    if (clip_max.X <= clip_min.X || clip_max.Y <= clip_min.Y)
                    {
                        continue;
                    }

                    RawRect rect = new((int)clip_min.X, (int)clip_min.Y, (int)clip_max.X, (int)clip_max.Y);
                    commandList.RSSetScissorRect(rect);

                    // TODO: texture resources
                    /*textureResources.TryGetValue(cmd.TextureId, out var texture);
                    if (texture != null)
                        ctx.PSSetShaderResources(0, texture);*/

                    commandList.DrawIndexedInstanced(cmd.ElemCount, 1, (uint)(cmd.IdxOffset + global_idx_offset), (int)(cmd.VtxOffset + global_vtx_offset), 0);
                }
            }
            global_idx_offset += cmdList.IdxBuffer.Size;
            global_vtx_offset += cmdList.VtxBuffer.Size;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _pipelineState.Dispose();

            _device.Release();

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct Constants(Matrix4x4 ProjectionMatrix);
}

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using MiniEngine.Logging;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using ImDrawIdx = ushort;

namespace MiniEngine.Graphics;

public unsafe class ImGuiRenderer : IDisposable
{
    private const int MaxBoundTextureViews = 126;

    private readonly D3D12Renderer _renderer;

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipelineState;

    private readonly ID3D12DescriptorHeap _resourceDescriptorHeap;
    private readonly uint _resourceDescriptorSize;

    private readonly RenderBuffers[] _renderBuffers;

    private readonly ID3D12Resource _constantBuffer;
    private byte* _constantsMemory = null;

    private int _nextViewId = 0;
    private readonly ConcurrentStack<int> _freedViewIds = [];

    private readonly nint _fontTextureId;
    private readonly ID3D12Resource _fontTexture;

    private bool _disposed;

    public ImGuiRenderer(D3D12Renderer renderer)
    {
        _renderer = renderer;

        RootSignatureFlags rootSignatureFlags = RootSignatureFlags.AllowInputAssemblerInputLayout
            | RootSignatureFlags.DenyHullShaderRootAccess
            | RootSignatureFlags.DenyDomainShaderRootAccess
            | RootSignatureFlags.DenyGeometryShaderRootAccess
            | RootSignatureFlags.DenyAmplificationShaderRootAccess
            | RootSignatureFlags.DenyMeshShaderRootAccess;

        RootDescriptorTable1 srvTable = new(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaxBoundTextureViews, 0, 0, 0));

        RootSignatureDescription1 rootSignatureDesc = new()
        {
            Flags = rootSignatureFlags,
            Parameters =
            [
                new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic), ShaderVisibility.Vertex),

                new(srvTable, ShaderVisibility.Pixel)
            ],
            StaticSamplers =
            [
                new StaticSamplerDescription(SamplerDescription.LinearClamp, ShaderVisibility.Pixel, 0, 0),
            ],
        };

        _rootSignature = _renderer.Device.CreateRootSignature(rootSignatureDesc);

        // ImGui needs two main shader resources:
        // Pixel shader:
        // 1. Texture (usually the font texture, but can be user defined)
        // 2. Texture sampler (LinearWrap)
        //
        // The vertex shader has a constant buffer, but we don't need to make a descriptor heap entry for it
        // We allocate +2 here for the ImGui textures
        _resourceDescriptorHeap = _renderer.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, MaxBoundTextureViews + 2, DescriptorHeapFlags.ShaderVisible));
        _resourceDescriptorSize = _renderer.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        // We actually have a separate cbuffer for each swapchain buffer
        // Later we update only the cbuffer for the current frameIndex
        uint cbufferSize = (uint)(Unsafe.SizeOf<Constants>() * D3D12Renderer.SwapChainBufferCount);
        _constantBuffer = _renderer.Device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(cbufferSize),
                ResourceStates.GenericRead);

        // Map the entire _constantBuffer, and d3d stores the pointer to that in _constantsMemory
        fixed (void* pMemory = &_constantsMemory)
        {
            _constantBuffer.Map(0, pMemory);
        }

        CpuDescriptorHandle resourceHandle = _resourceDescriptorHeap.GetCPUDescriptorHandleForHeapStart1();
        SamplerDescription samplerDesc = SamplerDescription.LinearClamp;
        _renderer.Device.CreateSampler(ref samplerDesc, resourceHandle);
        // We need to increment this here since the texture sampler is placed in the same descriptor heap
        _nextViewId++;

        CreateFontsTexture(out _fontTexture, out _fontTextureId);

        ShaderResourceViewDescription nullSrvDesc = new()
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new()
            {
                MipLevels = 1
            }
        };

        // We need to bind a null descriptor to ever slot of the descriptor heap that isn't being used
        // See https://www.siliceum.com/en/blog/post/d3d12_optimizing_null_descs/
        // See https://learn.microsoft.com/en-us/windows/win32/direct3d12/descriptors-overview#null-descriptors
        for (int i = _nextViewId + 1; i < MaxBoundTextureViews + 2; i++)
        {
            _renderer.Device.CreateShaderResourceView(null, nullSrvDesc, resourceHandle + (int)(i * _resourceDescriptorSize));
        }

        _renderBuffers = new RenderBuffers[D3D12Renderer.SwapChainBufferCount];
        for (int i = 0; i < D3D12Renderer.SwapChainBufferCount; i++)
        {
            _renderBuffers[i] = new RenderBuffers(_renderer.Device);
        }

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
            BlendState = BlendDescription.NonPremultiplied,
            // Disable depth and stencil
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm],
            SampleDescription = SampleDescription.Default,
        };

        _pipelineState = _renderer.Device.CreateGraphicsPipelineState(psoDesc);
    }

    private void CreateFontsTexture(out ID3D12Resource fontTexture, out nint fontTextureId)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height);

        Log.LogInfo($"Creating {nameof(ImGuiRenderer)} font texture with size {width}x{height}");
        fontTexture = _renderer.Device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)width, (uint)height),
                ResourceStates.CopyDest);
        fontTexture.Name = $"{nameof(ImGuiRenderer)} fontTexture";

        _renderer.CopyManager.QueueTexture2DUpload(fontTexture, Format.R8G8B8A8_UNorm, pixels, (uint)width, (uint)height).WaitOne();

        ShaderResourceViewDescription srvDesc = new()
        {
            Format = Format.R8G8B8A8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new()
            {
                MipLevels = 1,
                MostDetailedMip = 0,
            },
            Shader4ComponentMapping = ShaderComponentMapping.Default,
        };

        fontTextureId = BindTextureView(fontTexture, srvDesc);
        io.Fonts.SetTexID(fontTextureId);
    }

    public int BindTextureView(ID3D12Resource texture, ShaderResourceViewDescription viewDesc)
    {
        int viewId;

        // If there's been some descriptor slots unbound since, we use those first
        if (!_freedViewIds.TryPop(out viewId))
        {
            viewId = Interlocked.Increment(ref _nextViewId);
        }

        Log.LogInfo($"viewId: {viewId}");

        _renderer.Device.CreateShaderResourceView(texture, viewDesc, _resourceDescriptorHeap.GetCPUDescriptorHandleForHeapStart1() + (int)(viewId * _resourceDescriptorSize));

        return viewId;
    }

    public void UnbindTextureView(int viewId)
    {
        _freedViewIds.Push(viewId);

        Log.LogInfo($"viewId: {viewId}");

        ShaderResourceViewDescription nullSrvDesc = new()
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new()
            {
                MipLevels = 1
            }
        };

        // We need to bind a null descriptor to ever slot of the descriptor heap that isn't being used
        // See https://www.siliceum.com/en/blog/post/d3d12_optimizing_null_descs/
        // See https://learn.microsoft.com/en-us/windows/win32/direct3d12/descriptors-overview#null-descriptors
        _renderer.Device.CreateShaderResourceView(null, nullSrvDesc, _resourceDescriptorHeap.GetCPUDescriptorHandleForHeapStart1() + (int)(viewId * _resourceDescriptorSize));
    }

    public void PopulateCommandList(ID3D12GraphicsCommandList4 commandList, uint frameIndex, ImDrawDataPtr data)
    {
        RenderBuffers renderBuffers = _renderBuffers[frameIndex];

        renderBuffers.UpdateBuffers(data);

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

        commandList.IASetVertexBuffers(0, renderBuffers.VertexBufferView);
        commandList.IASetIndexBuffer(renderBuffers.IndexBufferView);
        commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        commandList.OMSetBlendFactor(Colors.Transparent);

        nint lastTextureId = -1;

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
                    Log.LogCrit($"User callback passed to ImGui but not implemented");
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

                    // Optimization to only set the descriptor table index when the texture id changes
                    if (cmd.TextureId != lastTextureId)
                    {
                        GpuDescriptorHandle textureHandle = _resourceDescriptorHeap.GetGPUDescriptorHandleForHeapStart1() + (int)(cmd.TextureId * _resourceDescriptorSize);

                        // 1 because the constant buffer view descriptor comes first
                        commandList.SetGraphicsRootDescriptorTable(1, textureHandle);
                        lastTextureId = cmd.TextureId;
                    }

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
            Log.LogInfo($"Disposing {nameof(ImGuiRenderer)}");

            // TODO: Figure out if we need to unmap _constantsMemory?
            _constantBuffer.Dispose();
            _fontTexture.Dispose();

            foreach (RenderBuffers renderBuffers in _renderBuffers)
            {
                renderBuffers.Dispose();
            }

            _resourceDescriptorHeap.Dispose();

            _rootSignature.Dispose();
            _pipelineState.Dispose();

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

    private class RenderBuffers : IDisposable
    {
        private readonly ID3D12Device _device;

        private ID3D12Resource? _vertexBuffer;
        private uint _vertexBufferSize = 2048;
        private ID3D12Resource? _indexBuffer;
        private uint _indexBufferSize = 2048;

        private bool _disposed;

        public VertexBufferView VertexBufferView => new(_vertexBuffer!.GPUVirtualAddress, (uint)Unsafe.SizeOf<ImDrawVert>() * _vertexBufferSize, (uint)Unsafe.SizeOf<ImDrawVert>());
        public IndexBufferView IndexBufferView => new(_indexBuffer!.GPUVirtualAddress, (uint)Unsafe.SizeOf<ImDrawIdx>() * _indexBufferSize, Format.R16_UInt);

        public RenderBuffers(ID3D12Device device)
        {
            _device = device;
        }

        public void UpdateBuffers(ImDrawDataPtr data)
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

                Log.LogInfo($"Creating ImGui vertex buffer with size {_vertexBufferSize}");
                _vertexBuffer = _device.CreateCommittedResource(
                    HeapType.Upload,
                    ResourceDescription.Buffer(vertexBufferSizeBytes),
                    ResourceStates.GenericRead);

                _vertexBuffer.Name = $"{nameof(ImGuiRenderer)} {nameof(_vertexBuffer)}";
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

                Log.LogInfo($"Creating ImGui index buffer with size {_indexBufferSize}");
                _indexBuffer = _device.CreateCommittedResource(
                    HeapType.Upload,
                    ResourceDescription.Buffer(indexBufferSizeBytes),
                    ResourceStates.GenericRead);

                _indexBuffer.Name = $"{nameof(ImGuiRenderer)} {nameof(_indexBuffer)}";
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
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _vertexBuffer?.Dispose();
                _indexBuffer?.Dispose();

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
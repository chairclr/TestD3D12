using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using MiniEngine.Logging;
using MiniEngine.Platform;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using ImDrawIdx = ushort;

namespace MiniEngine.Graphics;

public unsafe class ImGuiRenderer : IDisposable
{
    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _graphicsQueue;

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipelineState;

    private readonly ID3D12DescriptorHeap _resourceDescriptorHeap;
    private readonly uint _resourceDescriptorSize;

    private readonly RenderBuffers[] _renderBuffers;

    private readonly ID3D12Resource _constantBuffer;
    private byte* _constantsMemory = null;

    private nint _currentImTextureId = 0;
    private readonly Dictionary<nint, uint> _imTextureMap = [];

    private readonly nint _fontTextureId;
    private readonly ID3D12Resource _fontTexture;

    private bool _disposed;

    public ImGuiRenderer(ID3D12Device device, ID3D12CommandQueue graphicsQueue)
    {
        _device = device;
        _graphicsQueue = graphicsQueue;

        _device.AddRef();
        _graphicsQueue.AddRef();

        RootSignatureFlags rootSignatureFlags = RootSignatureFlags.AllowInputAssemblerInputLayout
            | RootSignatureFlags.DenyHullShaderRootAccess
            | RootSignatureFlags.DenyDomainShaderRootAccess
            | RootSignatureFlags.DenyGeometryShaderRootAccess
            | RootSignatureFlags.DenyAmplificationShaderRootAccess
            | RootSignatureFlags.DenyMeshShaderRootAccess;

        RootDescriptorTable1 srvTable = new(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0));

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

        CreateFontsTexture(resourceHandle, out _fontTexture, out _fontTextureId);
        resourceHandle += (int)_resourceDescriptorSize;

        SamplerDescription samplerDesc = SamplerDescription.LinearClamp;
        _device.CreateSampler(ref samplerDesc, resourceHandle);
        resourceHandle += (int)_resourceDescriptorSize;

        _renderBuffers = new RenderBuffers[D3D12Renderer.SwapChainBufferCount];
        for (int i = 0; i < D3D12Renderer.SwapChainBufferCount; i++)
        {
            _renderBuffers[i] = new RenderBuffers(_device);
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
            DepthStencilState = DepthStencilDescription.Default with
            {
                DepthEnable = false,
                StencilEnable = false
            },
            RenderTargetFormats = [Format.R8G8B8A8_UNorm],
            SampleDescription = SampleDescription.Default,
        };

        _pipelineState = _device.CreateGraphicsPipelineState(psoDesc);
    }

    // See https://github.com/ocornut/imgui/blob/master/backends/imgui_impl_dx12.cpp
    private void CreateFontsTexture(CpuDescriptorHandle resourceHandle, out ID3D12Resource fontTexture, out nint fontTextureId)
    {
        // See https://learn.microsoft.com/en-us/windows/win32/direct3d12/upload-and-readback-of-texture-data
        const uint D3D12_TEXTURE_DATA_PITCH_ALIGNMENT = 256;

        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height);

        Log.LogInfo($"Creating {nameof(ImGuiRenderer)} font texture with size {width}x{height}");
        fontTexture = _device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)width, (uint)height),
                ResourceStates.CopyDest);
        fontTexture.Name = $"{nameof(ImGuiRenderer)} fontTexture";

        uint upload_pitch = (uint)((width * 4 + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1u) & ~(D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1u));
        uint upload_size = (uint)(height * upload_pitch);

        using ID3D12Resource uploadBuffer = _device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer(upload_size),
                ResourceStates.GenericRead);

        void* mapped;
        Vortice.Direct3D12.Range readRange = new(0, 0);
        Vortice.Direct3D12.Range range = new(0, upload_size);
        uploadBuffer.Map(0, readRange, &mapped);

        for (int y = 0; y < height; y++)
        {
            Unsafe.CopyBlock((void*)((nint)mapped + y * upload_pitch), (void*)(pixels + y * width * 4), (uint)(width * 4));
        }

        uploadBuffer.Unmap(0, range);

        TextureCopyLocation srcLocation = new(uploadBuffer, new PlacedSubresourceFootPrint()
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, 1, upload_pitch)
        });

        TextureCopyLocation dstLocation = new(fontTexture, 0);

        WaitHandle fenceEvent = PlatformHelper.CreateAutoResetEvent(false);
        using ID3D12Fence fence = _device.CreateFence(0);
        using ID3D12CommandAllocator commandAllocator = _device.CreateCommandAllocator(CommandListType.Direct);
        using ID3D12GraphicsCommandList4 commandList = _device.CreateCommandList<ID3D12GraphicsCommandList4>(CommandListType.Direct, commandAllocator, null);

        commandList.CopyTextureRegion(dstLocation, 0, 0, 0, srcLocation);
        commandList.ResourceBarrierTransition(fontTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        commandList.Close();

        // Execute the command list.
        _graphicsQueue.ExecuteCommandList(commandList);
        _graphicsQueue.Signal(fence, 1);

        // Set the fence event and wait for the command list execution to finish
        fence.SetEventOnCompletion(1, fenceEvent);
        fenceEvent.WaitOne();

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

        _device.CreateShaderResourceView(fontTexture, srvDesc, resourceHandle);

        // TODO: Maybe dynamically allocate from the descriptor heap?
        fontTextureId = _currentImTextureId++;
        _imTextureMap.Add(fontTextureId, 1);
        io.Fonts.SetTexID(fontTextureId);
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
                        commandList.SetGraphicsRootDescriptorTable(_imTextureMap[cmd.TextureId], _resourceDescriptorHeap.GetGPUDescriptorHandleForHeapStart1());
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

            _graphicsQueue.Release();
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
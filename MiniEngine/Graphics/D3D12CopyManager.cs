using System.Runtime.CompilerServices;
using MiniEngine.Logging;
using MiniEngine.Platform;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace MiniEngine.Graphics;

public class D3D12CopyManager : IDisposable
{
    // See https://learn.microsoft.com/en-us/windows/win32/direct3d12/upload-and-readback-of-texture-data
    private const uint D3D12_TEXTURE_DATA_PITCH_ALIGNMENT = 256;

    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _commandQueue;

    private bool _disposed;

    private readonly List<StagingContext> _stagingContexts = [];
    private readonly Lock _stagingContextLock = new();

    public D3D12CopyManager(ID3D12Device device, ID3D12CommandQueue commandQueue)
    {
        _device = device;
        _commandQueue = commandQueue;

        _device.AddRef();
        _commandQueue.AddRef();
    }

    /// <summary>
    /// Uploads texture data in the <paramref name="data"/> to the <paramref name="destResource"/> from <paramref name="data"/>.
    ///
    /// Also transitions the resoures from <paramref name="before"/> to <paramref name="after"/>. Specify ResourceStates.None for either if this is not desired.
    /// </summary>
    public unsafe WaitHandle QueueTexture2DUpload(ID3D12Resource destResource, Format format, byte* data, uint width, uint height, ResourceStates before = ResourceStates.CopyDest, ResourceStates after = ResourceStates.PixelShaderResource)
    {
        // It's actually in bits, so / 8  for bytes
        uint pixelWidth = format.GetBitsPerPixel() / 8;

        uint uploadPitch = (width * pixelWidth + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1u) & ~(D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1u);
        uint uploadSize = height * uploadPitch;

        Log.LogInfo($"Width: {width}, height: {height}, format: {format}");

        // Look for a buffer that can handle the upload and succeedes in starting the upload (isn't busy)
        using (_stagingContextLock.EnterScope())
        {
            foreach (StagingContext context in _stagingContexts)
            {
                if (context.BufferSize <= uploadSize)
                {
                    bool startedUpload = context.Upload(commandList =>
                        UploadActionTexture2D(
                            commandList,
                            destResource,
                            format,
                            data,
                            width,
                            height,
                            before,
                            after,
                            pixelWidth,
                            uploadPitch,
                            uploadSize,
                            context));

                    if (startedUpload)
                    {
                        return context.FenceEvent;
                    }
                }
            }
        }

        // No buffer with enough size or that wasn't busy was found
        Log.LogInfo($"No free staging context with at least size {uploadSize} found, allocating new staging context");
        StagingContext newContext = new(_device, _commandQueue, uploadSize);

        // Add the new context to be reused
        using (_stagingContextLock.EnterScope())
        {
            _stagingContexts.Add(newContext);
        }

        newContext.Upload(commandList =>
            UploadActionTexture2D(
                commandList,
                destResource,
                format,
                data,
                width,
                height,
                before,
                after,
                pixelWidth,
                uploadPitch,
                uploadSize,
                newContext));

        return newContext.FenceEvent;
    }

    private static unsafe void UploadActionTexture2D(ID3D12GraphicsCommandList4 commandList, ID3D12Resource destResource, Format format, byte* data, uint width, uint height, ResourceStates before, ResourceStates after, uint pixelWidth, uint uploadPitch, uint uploadSize, StagingContext context)
    {
        void* mapped;
        Vortice.Direct3D12.Range readRange = new(0, 0);
        Vortice.Direct3D12.Range range = new(0, uploadSize);
        context.UploadBuffer.Map(0, readRange, &mapped);

        for (int y = 0; y < height; y++)
        {
            Unsafe.CopyBlock((void*)((nint)mapped + y * uploadPitch), data + y * width * pixelWidth, width * pixelWidth);
        }

        context.UploadBuffer.Unmap(0, range);

        TextureCopyLocation srcLocation = new(context.UploadBuffer, new PlacedSubresourceFootPrint()
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(format, width, height, 1, uploadPitch)
        });

        TextureCopyLocation dstLocation = new(destResource, 0);

        commandList.CopyTextureRegion(dstLocation, 0, 0, 0, srcLocation);

        // If the before or after state is none, don't transition
        if (before == ResourceStates.None || after == ResourceStates.None)
        {
            return;
        }
        commandList.ResourceBarrierTransition(destResource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            Log.LogInfo($"Disposing {nameof(D3D12CopyManager)}");

            foreach (StagingContext stagingContext in _stagingContexts)
            {
                stagingContext.Dispose();
            }

            _commandQueue.Release();
            _device.Release();

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    internal class StagingContext : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly ID3D12CommandQueue _commandQueue;

        private readonly ID3D12CommandAllocator _commandAllocator;
        private readonly ID3D12GraphicsCommandList4 _commandList;

        private readonly ID3D12Fence _fence;
        private ulong _fenceValue;

        private int _isExecuting = 0;

        public ID3D12Resource UploadBuffer { get; }
        public ulong BufferSize { get; }

        public WaitHandle FenceEvent { get; }

        private bool _disposed;

        public StagingContext(ID3D12Device device, ID3D12CommandQueue commandQueue, ulong size)
        {
            _device = device;
            _commandQueue = commandQueue;

            _fence = _device.CreateFence(_fenceValue);
            FenceEvent = PlatformHelper.CreateAutoResetEvent(false);
            _commandAllocator = _device.CreateCommandAllocator(CommandListType.Direct);
            _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList4>(CommandListType.Direct, _commandAllocator, null);
            _commandList.Close();

            BufferSize = size;

            UploadBuffer = _device.CreateCommittedResource(
                    HeapType.Upload,
                    ResourceDescription.Buffer(BufferSize),
                    ResourceStates.GenericRead);
        }

        public bool Upload(Action<ID3D12GraphicsCommandList4> uploadAction)
        {
            // If we're busy or have no work to do, don't do anything
            if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) == 1)
            {
                return false;
            }

            _commandAllocator.Reset();
            _commandList.Reset(_commandAllocator);

            uploadAction(_commandList);

            _commandList.Close();

            _fenceValue++;
            _commandQueue.ExecuteCommandList(_commandList);

            Task.Run(() =>
                {
                    // Wait until the command fence is set
                    WaitIdle();

                    // Resignal FenceEvent for anyone calling
                    // also do that before resetting _isExecuting
                    _commandQueue.Signal(_fence, _fenceValue);
                    _fence.SetEventOnCompletion(_fenceValue, FenceEvent);

                    // Reset _isExecuting to 0 (false) atomically
                    Interlocked.Exchange(ref _isExecuting, 0);

                    Log.LogInfo("Staging context finished upload");
                });

            return true;
        }

        public void WaitIdle()
        {
            _commandQueue.Signal(_fence, _fenceValue);
            _fence.SetEventOnCompletion(_fenceValue, FenceEvent);
            FenceEvent.WaitOne();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                WaitIdle();

                FenceEvent.Dispose();

                _commandAllocator.Dispose();
                _commandList.Dispose();
                UploadBuffer.Dispose();
                _fence.Dispose();

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private record UploadEvent(Action<ID3D12GraphicsCommandList4> UploadAction, ManualResetEventSlim FinishedEvent);
    }
}
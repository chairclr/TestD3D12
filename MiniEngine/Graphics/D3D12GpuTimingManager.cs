using MiniEngine.Logging;
using Vortice.Direct3D12;

namespace MiniEngine.Graphics;

public class D3D12GpuTimingManager : IDisposable
{
    private readonly ID3D12Device _device;
    private readonly ID3D12GraphicsCommandList4 _commandList;
    private readonly int _maxTimers;

    private readonly ID3D12QueryHeap _queryHeap;
    private readonly ID3D12Resource _readbackBuffer;

    private readonly ulong _timestampFreq = 1;

    private int _queryIndex = 0;
    private readonly Dictionary<string, GpuTimer> _gpuTimers = [];
    public IReadOnlyDictionary<string, GpuTimer> GpuTimers => _gpuTimers;

    private bool _disposed;

    public D3D12GpuTimingManager(ID3D12Device device, ID3D12GraphicsCommandList4 commandList, ID3D12CommandQueue commandQueue, int maxTimers = 64)
    {
        _device = device;
        _commandList = commandList;
        // * 2 here because each timer takes 2 slots (start, end)
        _maxTimers = maxTimers * 2;

        _device.AddRef();
        _commandList.AddRef();

        Log.LogInfo("Creating query heap and readback buffer");

        _queryHeap = device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, (uint)_maxTimers));
        _readbackBuffer = device.CreateCommittedResource(
                HeapProperties.ReadbackHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)(sizeof(ulong) * _maxTimers)),
                ResourceStates.CopyDest);

        commandQueue.GetTimestampFrequency(out _timestampFreq);

        Log.LogInfo($"Got commandQueue timestamp frequency: {_timestampFreq}");
    }

    /// <summary>
    /// Called at/before the start of every frame
    /// </summary>
    public void NewFrame()
    {
        _queryIndex = 0;
    }

    /// <summary>
    /// Call when you want to begin timing command list calls
    /// </summary>
    public void BeginTiming(string name)
    {
        if (!_gpuTimers.TryGetValue(name, out GpuTimer? timer))
        {
            _gpuTimers[name] = timer = new GpuTimer();
        }

        timer.StartIndex = _queryIndex++;
        _commandList.EndQuery(_queryHeap, QueryType.Timestamp, (uint)timer.StartIndex);
    }

    /// <summary>
    /// Call when you want to end timing command list calls, with the same name as the name as <see cref="BeginTiming(string)"/> 
    /// </summary>
    public void EndTiming(string name)
    {
        if (_gpuTimers.TryGetValue(name, out GpuTimer? timer))
        {
            timer.EndIndex = _queryIndex++;
            _commandList.EndQuery(_queryHeap, QueryType.Timestamp, (uint)timer.EndIndex);
        }
        else
        {
            Log.LogWarn($"No such timer {name}, did you forget to call BeginTiming?");
        }
    }

    /// <summary>
    /// Called after all rendering is done but before submitting the command list
    /// </summary>
    public void ResolveQueue()
    {
        _commandList.ResolveQueryData(_queryHeap, QueryType.Timestamp, 0, (uint)_queryIndex, _readbackBuffer, 0);
    }

    /// <summary>
    /// Called after the command list has finished executing
    /// </summary>
    public void EndFrame()
    {
        Span<ulong> data = _readbackBuffer.Map<ulong>(0, _queryIndex);

        foreach (GpuTimer timer in _gpuTimers.Values)
        {
            ulong delta = data[timer.EndIndex] - data[timer.StartIndex];
            timer.TimeMs = (double)delta / _timestampFreq * 1000.0;
        }

        _readbackBuffer.Unmap(0);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            Log.LogInfo($"Disposing {nameof(D3D12GpuTimingManager)}");

            _queryHeap.Dispose();
            _readbackBuffer.Dispose();

            _device.Release();
            _commandList.Release();

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public class GpuTimer
    {
        public int StartIndex;
        public int EndIndex;

        public double TimeMs;
    }
}
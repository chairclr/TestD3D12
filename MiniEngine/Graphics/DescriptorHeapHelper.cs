using SharpGen.Runtime;
using Vortice.Direct3D12;

namespace MiniEngine.Graphics;

public static class DescriptorHeapHelper
{
    private const uint GetCPUDescriptorHandleForHeapStart__vtbl_index = 9;
    private const uint GetGPUDescriptorHandleForHeapStart__vtbl_index = 10;

    public static CpuDescriptorHandle GetCPUDescriptorHandleForHeapStart1(this ID3D12DescriptorHeap heap)
    {
        CpuDescriptorHandle result;

        if (PlatformDetection.IsItaniumSystemV)
        {
            // Fixed behavior of normal Vortice ID3D12DescriptorHeap::GetCPUDescriptorHandleForHeapStart
            unsafe
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, CpuDescriptorHandle*, CpuDescriptorHandle*>)heap[GetCPUDescriptorHandleForHeapStart__vtbl_index];
                return *fn(heap.NativePointer, &result);
            }
        }

        return heap.GetCPUDescriptorHandleForHeapStart();
    }

    public static GpuDescriptorHandle GetGPUDescriptorHandleForHeapStart1(this ID3D12DescriptorHeap heap)
    {
        GpuDescriptorHandle result;

        if (PlatformDetection.IsItaniumSystemV)
        {
            // Fixed behavior of normal Vortice ID3D12DescriptorHeap::GetGPUDescriptorHandleForHeapStart
            unsafe
            {
                var fn = (delegate* unmanaged[Stdcall]<nint, GpuDescriptorHandle*, GpuDescriptorHandle*>)heap[GetGPUDescriptorHandleForHeapStart__vtbl_index];
                return *fn(heap.NativePointer, &result);
            }
        }

        return heap.GetGPUDescriptorHandleForHeapStart();
    }
}
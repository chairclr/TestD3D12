using System.Reflection;
using Vortice.Direct3D12;

namespace MiniEngine.Graphics;

public static class DescriptorHeapHelper
{
    private static readonly MethodInfo getVtbl = (typeof(ID3D12DescriptorHeap)
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .First(static x => x.Name == "Item")).GetMethod!;

    public static CpuDescriptorHandle GetCPUDescriptorHandleForHeapStart1(this ID3D12DescriptorHeap heap)
    {
        CpuDescriptorHandle result;

        // Fixed behavior of normal Vortice ID3D12DescriptorHeap::GetCPUDescriptorHandleForHeapStart
        unsafe
        {
            void* nativeCall = Pointer.Unbox(getVtbl.Invoke(heap, [9])!);

            ((delegate* unmanaged[Stdcall]<nint, void*, void*>)nativeCall)(heap.NativePointer, &result);
            return result;
        }
    }

    public static GpuDescriptorHandle GetGPUDescriptorHandleForHeapStart1(this ID3D12DescriptorHeap heap)
    {
        GpuDescriptorHandle result;

        // Fixed behavior of normal Vortice ID3D12DescriptorHeap::GetGPUDescriptorHandleForHeapStart
        unsafe
        {
            void* nativeCall = Pointer.Unbox(getVtbl.Invoke(heap, [10])!);

            ((delegate* unmanaged[Stdcall]<nint, void*, void*>)nativeCall)(heap.NativePointer, &result);
            return result;
        }
    }
}

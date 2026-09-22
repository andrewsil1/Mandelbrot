using System.ComponentModel;
using ComputeSharp;
using ComputeSharp.Interop;

namespace MandelbrotGpu;

// ComputeSharp 3.2's asynchronous fence callback completes its ValueTask without
// checking removal. Query the supported exported device after consuming it so
// a removal-signaled fence cannot be mistaken for successful shader completion.
internal sealed unsafe class GpuDeviceStatus : IDisposable
{
    private void* device;

    public GpuDeviceStatus(GraphicsDevice graphicsDevice)
    {
        Guid iid = new("189819F1-1DB6-4B57-BE54-1821339B85F7");
        void* pointer;
        InteropServices.GetID3D12Device(graphicsDevice, &iid, &pointer);
        device = pointer;
    }

    public void Check()
    {
        ObjectDisposedException.ThrowIf(device == null, this);
        // ID3D12Device inherits IUnknown/ID3D12Object. SDK d3d12.h's
        // ID3D12DeviceVtbl places GetDeviceRemovedReason at zero-based slot 37.
        void** methods = *(void***)device;
        int result = ((delegate* unmanaged[Stdcall]<void*, int>)methods[37])(device);
        CheckReason(result);
    }

    public static void CheckReason(int result)
    {
        if (result < 0) throw new Win32Exception(result);
    }

    public void Dispose()
    {
        if (device == null) return;
        void* pointer = device;
        device = null;
        // GetID3D12Device returns an AddRef'd interface; Release exactly once.
        ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)pointer)[2])(pointer);
    }
}

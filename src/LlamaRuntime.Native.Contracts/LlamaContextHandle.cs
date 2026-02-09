using System.Runtime.InteropServices;

namespace LlamaRuntime.Native.Contracts;

public sealed class LlamaContextHandle : SafeHandle
{
    private LlamaContextHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public static LlamaContextHandle FromIntPtr(IntPtr ptr)
    {
        var h = new LlamaContextHandle();
        h.SetHandle(ptr);
        return h;
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        try
        {
            NativeHandleReleaser.ReleaseContext?.Invoke(handle);
        }
        catch
        {
            // must not throw from ReleaseHandle
        }
        return true;
    }
}

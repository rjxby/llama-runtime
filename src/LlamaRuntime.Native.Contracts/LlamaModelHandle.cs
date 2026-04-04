using System.Runtime.InteropServices;

namespace LlamaRuntime.Native.Contracts;

public sealed class LlamaModelHandle : SafeHandle
{
    private LlamaModelHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public static LlamaModelHandle FromIntPtr(IntPtr ptr)
    {
        var h = new LlamaModelHandle();
        h.SetHandle(ptr);
        return h;
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        try
        {
            NativeRuntimeBindings.ReleaseModel(handle);
        }
        catch
        {
            // must not throw from ReleaseHandle
        }
        return true;
    }
}

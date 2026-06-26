namespace LlamaRuntime.Native.Contracts;

public class NativeCancelledException : NativeException
{
    public NativeCancelledException(string m) : base(NativeError.Cancelled, m) { }
}

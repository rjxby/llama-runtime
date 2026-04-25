namespace LlamaRuntime.Native.Contracts;

public class NativeNotImplementedException : NativeException
{
    public NativeNotImplementedException(string m) : base(NativeError.NotImplemented, m) { }
}

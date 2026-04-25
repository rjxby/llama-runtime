namespace LlamaRuntime.Native.Contracts;

public class NativeNotFoundException : NativeException
{
    public NativeNotFoundException(string m) : base(NativeError.NotFound, m) { }
}

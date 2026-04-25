namespace LlamaRuntime.Native.Contracts;

public class NativeOutOfMemoryException : NativeException
{
    public NativeOutOfMemoryException(string m) : base(NativeError.OutOfMemory, m) { }
}

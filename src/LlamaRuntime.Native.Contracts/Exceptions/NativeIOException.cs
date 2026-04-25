namespace LlamaRuntime.Native.Contracts;

public class NativeIOException : NativeException
{
    public NativeIOException(string m) : base(NativeError.Io, m) { }
}

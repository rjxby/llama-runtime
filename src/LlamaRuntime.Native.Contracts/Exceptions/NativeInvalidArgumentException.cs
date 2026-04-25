namespace LlamaRuntime.Native.Contracts;

public class NativeInvalidArgumentException : NativeException
{
    public NativeInvalidArgumentException(string m) : base(NativeError.InvalidArgument, m) { }
}

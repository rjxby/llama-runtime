namespace LlamaRuntime.Native.Contracts;

public class NativeBufferTooSmallException : NativeException
{
    public NativeBufferTooSmallException(string m) : base(NativeError.BufferTooSmall, m) { }
}

namespace LlamaRuntime.Native.Contracts;

public class NativeException : Exception
{
    public NativeError NativeErrorCode { get; }

    public NativeException(NativeError code, string message) : base(message) => NativeErrorCode = code;
}

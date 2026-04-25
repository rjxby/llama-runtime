namespace LlamaRuntime.Native.Contracts;

public class NativeUnknownException : NativeException
{
    public NativeUnknownException(string m) : base(NativeError.Unknown, m) { }
}

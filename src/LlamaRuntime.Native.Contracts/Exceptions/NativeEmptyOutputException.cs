namespace LlamaRuntime.Native.Contracts;

public class NativeEmptyOutputException : NativeException
{
    public NativeEmptyOutputException(string m) : base(NativeError.EmptyOutput, m) { }
}

namespace LlamaRuntime.Native.Contracts;

public class NativeLoadModelException : NativeException
{
    public NativeLoadModelException(string m) : base(NativeError.LoadModel, m) { }
}

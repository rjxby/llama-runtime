namespace LlamaRuntime.Native.Contracts;

public class NativeInferException : NativeException
{
    public NativeInferException(string m) : base(NativeError.Infer, m) { }
}

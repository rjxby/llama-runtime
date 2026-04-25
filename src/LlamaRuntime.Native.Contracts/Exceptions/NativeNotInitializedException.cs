namespace LlamaRuntime.Native.Contracts;

public class NativeNotInitializedException : NativeException
{
    public NativeNotInitializedException(string m) : base(NativeError.NotInitialized, m) { }
}

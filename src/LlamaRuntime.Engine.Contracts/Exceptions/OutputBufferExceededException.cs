namespace LlamaRuntime.Engine.Contracts;

public sealed class OutputBufferExceededException : InferenceException
{
    public OutputBufferExceededException(string message, Exception? inner = null) : base(message, inner) { }
}

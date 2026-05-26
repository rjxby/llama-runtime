namespace LlamaRuntime.Engine.Contracts;

public sealed class StructuredOutputException : InferenceException
{
    public StructuredOutputException(string message, Exception? inner = null) : base(message, inner) { }
}

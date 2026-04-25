namespace LlamaRuntime.Engine.Contracts;

public sealed class EmptyInferenceOutputException : InferenceException
{
    public EmptyInferenceOutputException(string message) : base(message) { }
}

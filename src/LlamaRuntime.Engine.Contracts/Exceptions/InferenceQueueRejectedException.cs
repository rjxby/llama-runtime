namespace LlamaRuntime.Engine.Contracts;

public sealed class InferenceQueueRejectedException : InferenceException
{
    public InferenceQueueRejectedException(string message, Exception? inner = null) : base(message, inner) { }
}

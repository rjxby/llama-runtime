namespace LlamaRuntime.Engine.Contracts;

public class InferenceException : EngineException
{
    public InferenceException(string message, Exception? inner = null) : base(message, inner) { }
}

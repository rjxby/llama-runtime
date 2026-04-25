namespace LlamaRuntime.Engine.Contracts;

public class ModelNotFoundException : EngineException
{
    public ModelNotFoundException(string message, Exception? inner = null) : base(message, inner) { }
}

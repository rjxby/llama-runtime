namespace LlamaRuntime.Engine.Contracts;

public class ModelLoadException : EngineException
{
    public ModelLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

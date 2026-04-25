namespace LlamaRuntime.Engine.Contracts;

public class EngineException : Exception
{
    public EngineException(string message, Exception? inner = null) : base(message, inner) { }
}

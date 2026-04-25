namespace LlamaRuntime.Engine.Contracts;

public class PoolExhaustedException : EngineException
{
    public PoolExhaustedException(string message, Exception? inner = null) : base(message, inner) { }
}

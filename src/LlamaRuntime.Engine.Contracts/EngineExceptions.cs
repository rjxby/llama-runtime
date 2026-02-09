namespace LlamaRuntime.Engine.Contracts;

public class EngineException : Exception
{
    public EngineException(string message, Exception? inner = null) : base(message, inner) { }
}

public class ModelLoadException : EngineException
{
    public ModelLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

public class ModelNotFoundException : EngineException
{
    public ModelNotFoundException(string message, Exception? inner = null) : base(message, inner) { }
}

public class InferenceException : EngineException
{
    public InferenceException(string message, Exception? inner = null) : base(message, inner) { }
}

public class PoolExhaustedException : EngineException
{
    public PoolExhaustedException(string message, Exception? inner = null) : base(message, inner) { }
}

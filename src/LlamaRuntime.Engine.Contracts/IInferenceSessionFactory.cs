namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// Factory for creating inference sessions.
/// </summary>
public interface IInferenceSessionFactory
{
    /// <summary>
    /// Acquires an isolated inference session for the specified model.
    /// </summary>
    Task<IInferenceSession> CreateSessionAsync(IEngineModel model, CancellationToken ct = default);
}

namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// Manages per-model context pools and leases isolated sessions for individual requests.
/// </summary>
public interface ILlamaContextManager : IDisposable
{
    /// <summary>
    /// Acquires an isolated session backed by a single pooled context lease.
    /// </summary>
    Task<IInferenceSession> CreateSessionAsync(IEngineModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up any resources associated with the specified model.
    /// </summary>
    void ReleaseModelResources(IEngineModel model);
}

using LlamaRuntime.Native.Contracts;

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
    /// Seeds the pool for a model with an already-created context so load-time probes are reused.
    /// </summary>
    void PrimeModelContext(IEngineModel model, LlamaContextHandle context);

    /// <summary>
    /// Cleans up any resources associated with the specified model.
    /// </summary>
    void ReleaseModelResources(IEngineModel model);
}

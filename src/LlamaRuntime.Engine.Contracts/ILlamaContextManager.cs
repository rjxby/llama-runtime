using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// Contract for managing llama_context lifecycles and pools.
/// </summary>
public interface ILlamaContextManager : IDisposable
{
    /// <summary>
    /// Executes the provided action within a managed context lease.
    /// </summary>
    Task<TResult> WithContextAsync<TResult>(IEngineModel model, Func<LlamaContextHandle, Task<TResult>> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up any resources associated with the specified model.
    /// </summary>
    void ReleaseModelResources(IEngineModel model);
}

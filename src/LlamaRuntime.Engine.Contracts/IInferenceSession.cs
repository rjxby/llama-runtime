namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// Represents an isolated inference session backed by a single leased context.
/// </summary>
public interface IInferenceSession : IAsyncDisposable
{
    Task<int> CountTokensAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// Executes inference within this isolated context.
    /// </summary>
    Task<InferenceResult> InferAsync(string prompt, CancellationToken ct = default);
}

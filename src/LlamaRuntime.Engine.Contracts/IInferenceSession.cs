namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// Represents an isolated inference session (Unit of Work).
/// </summary>
public interface IInferenceSession : IAsyncDisposable
{
    Task<int> CountTokensAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// Executes inference within this isolated context.
    /// </summary>
    Task<string> InferAsync(string prompt, CancellationToken ct = default);
}

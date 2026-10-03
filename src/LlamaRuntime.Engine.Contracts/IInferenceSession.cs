namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// Represents an isolated inference session backed by a single leased context.
/// </summary>
public interface IInferenceSession : IAsyncDisposable
{
    Task<int> CountTokensAsync(string prompt, CancellationToken ct = default);

    Task<InferenceResult> InferAsync(
        string prompt,
        CancellationToken ct = default,
        InferenceResponseFormat responseFormat = InferenceResponseFormat.Text,
        string? grammar = null,
        InferenceGenerationOptions? generationOptions = null);
}

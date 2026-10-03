namespace LlamaRuntime.Engine.Contracts;

public interface ILlamaProvider : IDisposable
{
    Task<IEngineModel> LoadModelAsync(string path, CancellationToken cancellationToken = default);
    Task UnloadModelAsync(IEngineModel model, CancellationToken cancellationToken = default);
    Task<int> CountTokensAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default);
    Task<InferenceResult> InferAsync(
        IEngineModel model,
        PreparedGenerationRequest request,
        CancellationToken cancellationToken = default);
}

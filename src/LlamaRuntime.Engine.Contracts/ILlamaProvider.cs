namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// High-level engine provider: load/unload models and perform inference.
/// </summary>
public interface ILlamaProvider : IInferenceSessionFactory, IDisposable
{
    Task<IEngineModel> LoadModelAsync(string path, CancellationToken cancellationToken = default);
    Task UnloadModelAsync(IEngineModel model, CancellationToken cancellationToken = default);
    Task<int> CountTokensAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default);
    Task<string> InferAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default);
}

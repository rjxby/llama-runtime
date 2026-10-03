namespace LlamaRuntime.Engine.Contracts;

public interface IEngineModel : IDisposable, IAsyncDisposable
{
    Task<IInferenceSession> CreateSessionAsync(CancellationToken cancellationToken = default);

    string SourcePath { get; }
    ModelMetadata Metadata { get; }
}

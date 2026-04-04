namespace LlamaRuntime.Presentation.Grpc.Services;

public interface IInferenceExecutor
{
    Task<string> InferAsync(string prompt, CancellationToken cancellationToken);
    Task<int> CountTokensAsync(string prompt, CancellationToken cancellationToken);
}

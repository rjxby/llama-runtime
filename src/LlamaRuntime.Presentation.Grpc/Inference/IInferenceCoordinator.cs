using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.Inference;

public interface IInferenceCoordinator
{
    Task<InferenceResult> InferAsync(string prompt, CancellationToken cancellationToken, string? requestId = null);

    Task<int> CountTokensAsync(string prompt, CancellationToken cancellationToken, string? requestId = null);
}

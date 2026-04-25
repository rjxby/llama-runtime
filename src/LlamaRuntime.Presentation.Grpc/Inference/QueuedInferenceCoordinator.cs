using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.Inference;

public sealed class QueuedInferenceCoordinator : IInferenceCoordinator
{
    private readonly ILlamaProvider _provider;
    private readonly InferenceWorkQueue _queue;

    public QueuedInferenceCoordinator(ILlamaProvider provider, InferenceWorkQueue queue)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public Task<InferenceResult> InferAsync(string prompt, CancellationToken cancellationToken, string? requestId = null) =>
        _queue.EnqueueAsync(
            InferenceWorkQueue.InferenceOperation.Infer,
            (model, ct) => _provider.InferAsync(model, prompt, ct),
            cancellationToken,
            requestId);

    public Task<int> CountTokensAsync(string prompt, CancellationToken cancellationToken, string? requestId = null) =>
        _queue.EnqueueAsync(
            InferenceWorkQueue.InferenceOperation.CountTokens,
            (model, ct) => _provider.CountTokensAsync(model, prompt, ct),
            cancellationToken,
            requestId);
}

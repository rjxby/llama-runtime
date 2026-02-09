namespace LlamaRuntime.Presentation.Grpc.Managers;

public interface IInferenceManager
{
    Task<string> EnqueueAsync(string requestId, string prompt, CancellationToken ct);
    IAsyncEnumerable<InferenceRequest> ReadRequestsAsync(CancellationToken ct);
}

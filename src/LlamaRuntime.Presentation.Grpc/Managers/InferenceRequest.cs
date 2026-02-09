namespace LlamaRuntime.Presentation.Grpc.Managers;

public sealed record InferenceRequest(
    string RequestId,
    string Prompt,
    TaskCompletionSource<string> CompletionSource,
    CancellationToken CancellationToken);

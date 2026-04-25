using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.Inference;

public sealed partial class InferenceWorkQueue
{
    internal interface IInferenceWorkItem
    {
        InferenceOperation Operation { get; }
        string? RequestId { get; }
        long EnqueuedAt { get; }
        CancellationToken CallerCancellationToken { get; }
        Task ExecuteAsync(IEngineModel model, CancellationToken cancellationToken);
        void TrySetException(Exception exception);
        void TrySetCanceled(CancellationToken cancellationToken);
    }
}

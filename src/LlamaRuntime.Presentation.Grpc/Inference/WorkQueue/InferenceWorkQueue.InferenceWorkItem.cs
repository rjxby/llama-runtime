using System.Diagnostics;
using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.Inference;

public sealed partial class InferenceWorkQueue
{
    private sealed class InferenceWorkItem<T> : IInferenceWorkItem
    {
        private readonly Func<IEngineModel, CancellationToken, Task<T>> _work;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InferenceWorkItem(
            InferenceOperation operation,
            Func<IEngineModel, CancellationToken, Task<T>> work,
            CancellationToken callerCancellationToken,
            string? requestId)
        {
            Operation = operation;
            _work = work;
            CallerCancellationToken = callerCancellationToken;
            RequestId = requestId;
            EnqueuedAt = Stopwatch.GetTimestamp();
        }

        public InferenceOperation Operation { get; }

        public string? RequestId { get; }

        public long EnqueuedAt { get; }

        public CancellationToken CallerCancellationToken { get; }

        public async Task ExecuteAsync(IEngineModel model, CancellationToken cancellationToken)
        {
            var result = await _work(model, cancellationToken).ConfigureAwait(false);
            _tcs.TrySetResult(result);
        }

        public void TrySetException(Exception exception) => _tcs.TrySetException(exception);

        public void TrySetCanceled(CancellationToken cancellationToken) => _tcs.TrySetCanceled(cancellationToken);

        public Task<T> WaitAsync(CancellationToken cancellationToken) => _tcs.Task.WaitAsync(cancellationToken);
    }
}

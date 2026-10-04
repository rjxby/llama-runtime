using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;

namespace LlamaRuntime.Presentation.Grpc.Inference;

public sealed class InferenceWorkQueue
{
    private readonly Channel<IInferenceWorkItem> _channel;
    private readonly InferenceOptions _options;
    private readonly CancellationTokenSource _stopping = new();

    public InferenceWorkQueue(IOptions<InferenceOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (_options.ChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference channel capacity must be greater than zero.");
        }

        if (_options.AcquireTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference queue acquire timeout must be greater than zero.");
        }

        _channel = Channel.CreateBounded<IInferenceWorkItem>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    internal ValueTask<IInferenceWorkItem> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    internal void Complete()
    {
        if (!_channel.Writer.TryComplete())
        {
            return;
        }

        _stopping.Cancel();
        while (_channel.Reader.TryRead(out var queued))
        {
            queued.TrySetCanceled(_stopping.Token);
        }
    }

    internal async Task<T> EnqueueAsync<T>(
        InferenceOperation operation,
        Func<IEngineModel, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken,
        string? requestId)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var workItem = new InferenceWorkItem<T>(operation, work, cancellationToken, requestId);
        using var enqueueCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        enqueueCts.CancelAfter(_options.AcquireTimeout);

        try
        {
            await _channel.Writer.WriteAsync(workItem, enqueueCts.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new InferenceQueueRejectedException("Inference queue is closed because the runtime is stopping.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InferenceQueueRejectedException(
                $"Inference queue did not accept the request within {_options.AcquireTimeout}.",
                ex);
        }

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            return await workItem.WaitAsync(waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _stopping.IsCancellationRequested)
        {
            throw new ModelNotFoundException("Model is stopping.");
        }
    }

    internal enum InferenceOperation
    {
        Infer = 0,
        CountTokens = 1
    }

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

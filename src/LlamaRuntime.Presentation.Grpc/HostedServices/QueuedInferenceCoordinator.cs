using System.Threading.Channels;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.HostedServices;

public sealed class QueuedInferenceCoordinator : BackgroundService
{
    private readonly Channel<IInferenceWorkItem> _channel;
    private readonly ILlamaProvider _provider;
    private readonly IHostedModelStateReader _hostedModelReader;
    private readonly ILogger<QueuedInferenceCoordinator> _logger;
    private readonly InferenceOptions _options;

    public QueuedInferenceCoordinator(
        ILlamaProvider provider,
        IHostedModelStateReader hostedModelReader,
        IOptions<InferenceOptions> options,
        ILogger<QueuedInferenceCoordinator> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _hostedModelReader = hostedModelReader ?? throw new ArgumentNullException(nameof(hostedModelReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (_options.ChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference channel capacity must be greater than zero.");
        }

        if (_options.WorkerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference worker count must be greater than zero.");
        }

        _logger.LogInformation(
            "QueuedInferenceCoordinator configured (worker_count={WorkerCount}, effective_native_parallelism={EffectiveNativeParallelism}, channel_capacity={ChannelCapacity}, acquire_timeout_ms={AcquireTimeoutMs})",
            _options.WorkerCount,
            _options.WorkerCount,
            _options.ChannelCapacity,
            _options.AcquireTimeout.TotalMilliseconds);

        _channel = Channel.CreateBounded<IInferenceWorkItem>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public Task<InferenceResult> InferAsync(string prompt, CancellationToken cancellationToken, string? requestId = null) =>
        EnqueueAsync(
            "infer",
            (model, ct) => _provider.InferAsync(model, prompt, ct),
            cancellationToken,
            requestId);

    public Task<int> CountTokensAsync(string prompt, CancellationToken cancellationToken, string? requestId = null) =>
        EnqueueAsync(
            "count_tokens",
            (model, ct) => _provider.CountTokensAsync(model, prompt, ct),
            cancellationToken,
            requestId);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken));

        return Task.WhenAll(workers);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> EnqueueAsync<T>(
        string operationName,
        Func<IEngineModel, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        string? requestId)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var workItem = new InferenceWorkItem<T>(operationName, operation, cancellationToken, requestId);
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

        return await workItem.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var workItem in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                workItem.TrySetCanceled(stoppingToken);
                continue;
            }

            if (workItem.CallerCancellationToken.IsCancellationRequested)
            {
                workItem.TrySetCanceled(workItem.CallerCancellationToken);
                continue;
            }

            if (!_hostedModelReader.TryGetLoadedModel(out var model) || model == null)
            {
                workItem.TrySetException(CreateModelUnavailableException());
                continue;
            }

            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, workItem.CallerCancellationToken);
            var queueWaitMs = Stopwatch.GetElapsedTime(workItem.EnqueuedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Inference work item started (operation={OperationName}, request_id={RequestId}, worker_id={WorkerId}, queue_wait_ms={QueueWaitMs})",
                workItem.OperationName,
                workItem.RequestId ?? string.Empty,
                workerId,
                queueWaitMs);

            try
            {
                await workItem.ExecuteAsync(model, executionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workItem.CallerCancellationToken.IsCancellationRequested)
            {
                workItem.TrySetCanceled(workItem.CallerCancellationToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                workItem.TrySetCanceled(stoppingToken);
            }
            catch (Exception ex)
            {
                workItem.TrySetException(ex);
            }
        }
    }

    private ModelNotFoundException CreateModelUnavailableException()
    {
        var snapshot = _hostedModelReader.GetSnapshot();
        return snapshot.State switch
        {
            HostedModelState.Loading => new ModelNotFoundException("Model is still loading."),
            HostedModelState.WarmingUp => new ModelNotFoundException("Model is warming up."),
            HostedModelState.Failed => new ModelNotFoundException(snapshot.FailureMessage ?? "Model failed to load."),
            HostedModelState.Stopping => new ModelNotFoundException("Model is stopping."),
            _ => new ModelNotFoundException("Model not loaded.")
        };
    }

    private interface IInferenceWorkItem
    {
        string OperationName { get; }
        string? RequestId { get; }
        long EnqueuedAt { get; }
        CancellationToken CallerCancellationToken { get; }
        Task ExecuteAsync(IEngineModel model, CancellationToken cancellationToken);
        void TrySetException(Exception exception);
        void TrySetCanceled(CancellationToken cancellationToken);
    }

    private sealed class InferenceWorkItem<T> : IInferenceWorkItem
    {
        private readonly Func<IEngineModel, CancellationToken, Task<T>> _operation;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InferenceWorkItem(
            string operationName,
            Func<IEngineModel, CancellationToken, Task<T>> operation,
            CancellationToken callerCancellationToken,
            string? requestId)
        {
            OperationName = operationName;
            _operation = operation;
            CallerCancellationToken = callerCancellationToken;
            RequestId = requestId;
            EnqueuedAt = Stopwatch.GetTimestamp();
        }

        public string OperationName { get; }

        public string? RequestId { get; }

        public long EnqueuedAt { get; }

        public CancellationToken CallerCancellationToken { get; }

        public async Task ExecuteAsync(IEngineModel model, CancellationToken cancellationToken)
        {
            var result = await _operation(model, cancellationToken).ConfigureAwait(false);
            _tcs.TrySetResult(result);
        }

        public void TrySetException(Exception exception) => _tcs.TrySetException(exception);

        public void TrySetCanceled(CancellationToken cancellationToken) => _tcs.TrySetCanceled(cancellationToken);

        public Task<T> WaitAsync(CancellationToken cancellationToken) => _tcs.Task.WaitAsync(cancellationToken);
    }
}

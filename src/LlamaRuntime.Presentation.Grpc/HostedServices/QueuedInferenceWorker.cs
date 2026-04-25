using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.Inference;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.HostedServices;

public sealed class QueuedInferenceWorker : BackgroundService
{
    private readonly InferenceWorkQueue _queue;
    private readonly IHostedModel _hostedModel;
    private readonly ILogger<QueuedInferenceWorker> _logger;
    private readonly InferenceOptions _options;

    public QueuedInferenceWorker(
        InferenceWorkQueue queue,
        IHostedModel hostedModel,
        IOptions<InferenceOptions> options,
        ILogger<QueuedInferenceWorker> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _hostedModel = hostedModel ?? throw new ArgumentNullException(nameof(hostedModel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (_options.WorkerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference worker count must be greater than zero.");
        }

        _logger.LogInformation(
            "QueuedInferenceWorker configured (worker_count={WorkerCount}, effective_native_parallelism={EffectiveNativeParallelism}, channel_capacity={ChannelCapacity}, acquire_timeout_ms={AcquireTimeoutMs})",
            _options.WorkerCount,
            _options.WorkerCount,
            _options.ChannelCapacity,
            _options.AcquireTimeout.TotalMilliseconds);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken));

        return Task.WhenAll(workers);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            InferenceWorkQueue.IInferenceWorkItem workItem;
            try
            {
                workItem = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

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

            if (!_hostedModel.TryGetLoadedModel(out var model) || model == null)
            {
                workItem.TrySetException(CreateModelUnavailableException());
                continue;
            }

            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, workItem.CallerCancellationToken);
            var queueWaitMs = Stopwatch.GetElapsedTime(workItem.EnqueuedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Inference work item started (operation={OperationName}, request_id={RequestId}, worker_id={WorkerId}, queue_wait_ms={QueueWaitMs})",
                workItem.Operation,
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
        var snapshot = _hostedModel.GetSnapshot();
        return snapshot.State switch
        {
            HostedModelState.Loading => new ModelNotFoundException("Model is still loading."),
            HostedModelState.WarmingUp => new ModelNotFoundException("Model is warming up."),
            HostedModelState.Failed => new ModelNotFoundException(snapshot.FailureMessage ?? "Model failed to load."),
            HostedModelState.Stopping => new ModelNotFoundException("Model is stopping."),
            _ => new ModelNotFoundException("Model not loaded.")
        };
    }
}

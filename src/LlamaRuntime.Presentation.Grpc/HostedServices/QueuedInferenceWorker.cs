using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.Inference;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.HostedServices;

public sealed class QueuedInferenceWorker : BackgroundService
{
    private readonly InferenceWorkQueue _queue;
    private readonly IHostedModel _hostedModel;
    private readonly ILogger<QueuedInferenceWorker> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly InferenceOptions _options;
    private readonly ILlamaProvider _provider;
    private readonly LlamaNativeOptions _nativeOptions;
    private readonly IHostedRuntimeInfo _hostedRuntimeInfo;
    private readonly HostedModelOptions _hostedOptions;
    private readonly CancellationTokenSource _shutdown = new();
    private int _stopInitiated;

    public QueuedInferenceWorker(
        InferenceWorkQueue queue,
        IHostedModel hostedModel,
        IOptions<InferenceOptions> options,
        ILogger<QueuedInferenceWorker> logger,
        IHostApplicationLifetime applicationLifetime,
        ILlamaProvider provider,
        IHostedRuntimeInfo hostedRuntimeInfo,
        IOptions<HostedModelOptions> hostedOptions,
        IOptions<LlamaNativeOptions> nativeOptions)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _hostedRuntimeInfo = hostedRuntimeInfo ?? throw new ArgumentNullException(nameof(hostedRuntimeInfo));
        _hostedOptions = hostedOptions?.Value ?? throw new ArgumentNullException(nameof(hostedOptions));
        _nativeOptions = nativeOptions?.Value ?? throw new ArgumentNullException(nameof(nativeOptions));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _hostedModel = hostedModel ?? throw new ArgumentNullException(nameof(hostedModel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (_options.WorkerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference worker count must be greater than zero.");
        }

        _logger.LogInformation(
            nameof(QueuedInferenceWorker) + " configured (worker_count={WorkerCount}, effective_native_parallelism={EffectiveNativeParallelism}, channel_capacity={ChannelCapacity}, acquire_timeout_ms={AcquireTimeoutMs})",
            _options.WorkerCount,
            _options.WorkerCount,
            _options.ChannelCapacity,
            _options.AcquireTimeout.TotalMilliseconds);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopInitiated) != 0, this);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        _hostedModel.SetLoading();
        try
        {
            var model = await _provider.LoadModelAsync(_hostedOptions.ModelPath, startup.Token).ConfigureAwait(false);
            try
            {
                _hostedModel.SetWarmingUp(model, _hostedOptions.ModelId);
            }
            catch
            {
                await model.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            LogDiscoveredModelMetadata();
            var prepared = new PreparedGenerationRequest(_options.StartupWarmupPrompt,
                new InferenceGenerationOptions(_nativeOptions.GenerationMaxNewTokens, 0.0f, 1.0f));
            var warmup = await _provider.InferAsync(model, prepared, startup.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(warmup.Content))
            {
                throw new EmptyInferenceOutputException("Startup warm-up returned blank output.");
            }

            startup.Token.ThrowIfCancellationRequested();
            _hostedModel.SetLoaded(model, _hostedOptions.ModelId);
            LogCapabilityDiagnostics();
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await _hostedModel.CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                _hostedModel.SetFailed(ex);
            }

            _logger.LogError(ex, "Failed to load model during startup");
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.Token);
        var workers = Enumerable.Range(0, _options.WorkerCount).Select(workerId => Task.Run(async () =>
        {
            try
            {
                await RunWorkerAsync(workerId, execution.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                BeginStop();
                _applicationLifetime.StopApplication();
                try
                {
                    _logger.LogError(ex, "Inference worker failed (worker_id={WorkerId})", workerId);
                }
                catch (Exception loggingFailure)
                {
                    throw new AggregateException(ex, loggingFailure);
                }

                throw;
            }
        })).ToArray();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            BeginStop();
            await _hostedModel.CloseAsync().ConfigureAwait(false);
            _hostedModel.Reset();
        }
    }

    private void BeginStop()
    {
        if (Interlocked.Exchange(ref _stopInitiated, 1) != 0)
        {
            return;
        }

        _hostedModel.SetStopping();
        _queue.Complete();
        _shutdown.Cancel();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        BeginStop();
        base.Dispose();
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

            try
            {
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
                catch (Exception ex) when (ex is InferenceException or NativeException or OperationCanceledException)
                {
                    workItem.TrySetException(ex);
                }
            }
            catch (Exception ex)
            {
                workItem.TrySetException(ex);
                throw;
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
    private void LogDiscoveredModelMetadata()
    {
        var runtimeInfo = _hostedRuntimeInfo.GetRuntimeInfo();
        _logger.LogInformation(
            "Loaded model metadata for model {ModelId}: configured_runtime_context_size={ConfiguredRuntimeContextSize}, effective_runtime_context_size={EffectiveRuntimeContextSize}, training_context_size={TrainingContextSize}, tokenizer_family={TokenizerFamily}",
            runtimeInfo.PublicModelId,
            runtimeInfo.ConfiguredContextSize,
            runtimeInfo.EffectiveContextSize,
            runtimeInfo.TrainingContextSize ?? 0,
            runtimeInfo.TokenizerFamily);
    }

    private void LogCapabilityDiagnostics()
    {
        var runtimeInfo = _hostedRuntimeInfo.GetRuntimeInfo();
        _logger.LogInformation(
            "Runtime capabilities for model {ModelId}: context_size={ContextSize}, tokenizer_family={TokenizerFamily}, supports_structured_output={SupportsStructuredOutput}, supports_json_output={SupportsJsonOutput}, supports_speculative_decoding={SupportsSpeculativeDecoding}",
            runtimeInfo.PublicModelId,
            runtimeInfo.EffectiveContextSize,
            runtimeInfo.TokenizerFamily,
            runtimeInfo.StructuredOutput.IsSupported,
            runtimeInfo.JsonOutput.IsSupported,
            runtimeInfo.SpeculativeDecoding.IsSupported);

        LogUnsupportedCapability(runtimeInfo, "structured_output", runtimeInfo.StructuredOutput);
        LogUnsupportedCapability(runtimeInfo, "json_output", runtimeInfo.JsonOutput);
        LogUnsupportedCapability(runtimeInfo, "speculative_decoding", runtimeInfo.SpeculativeDecoding);
    }

    private void LogUnsupportedCapability(
        HostedRuntimeInfo runtimeInfo,
        string capabilityName,
        RuntimeCapabilityStatus capability)
    {
        if (capability.IsSupported)
        {
            return;
        }

        _logger.LogInformation(
            "Runtime capability {CapabilityName} is unavailable for model {ModelId}: {Diagnostic}",
            capabilityName,
            runtimeInfo.PublicModelId,
            capability.Diagnostic);
    }
}

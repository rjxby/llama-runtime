using System.Threading.Channels;
using Microsoft.Extensions.Options;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.Managers;

namespace LlamaRuntime.Presentation.Grpc.HostedServices;

internal sealed class InferenceWorker : BackgroundService
{
    private readonly IInferenceManager _manager;
    private readonly ILlamaProvider _provider;
    private readonly ILoadedModelAccessor _accessor;
    private readonly ILogger<InferenceWorker> _logger;
    private readonly InferenceOptions _options;

    public InferenceWorker(
        IInferenceManager manager,
        ILlamaProvider provider,
        ILoadedModelAccessor accessor,
        IOptions<InferenceOptions> options,
        ILogger<InferenceWorker> logger)
    {
        _manager = manager;
        _provider = provider;
        _accessor = accessor;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(i => Task.Run(() => ProcessQueueAsync(i, stoppingToken), stoppingToken))
            .ToList();

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(int workerId, CancellationToken ct)
    {
        _logger.LogInformation("Inference worker {WorkerId} started", workerId);

        try
        {
            await foreach (var request in _manager.ReadRequestsAsync(ct).ConfigureAwait(false))
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, request.CancellationToken);
                var linkedToken = linkedCts.Token;

                try
                {
                    var model = _accessor.Model;
                    if (model == null)
                    {
                        request.CompletionSource.TrySetException(new InvalidOperationException("Model not loaded"));
                        continue;
                    }

                    var result = await _provider.InferAsync(model, request.Prompt, linkedToken).ConfigureAwait(false);
                    request.CompletionSource.TrySetResult(result);
                }
                catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
                {
                    request.CompletionSource.TrySetCanceled(linkedToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inference failed for request {RequestId} in worker {WorkerId}", request.RequestId, workerId);
                    request.CompletionSource.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in worker {WorkerId}", workerId);
        }

        _logger.LogInformation("Inference worker {WorkerId} stopped", workerId);
    }
}

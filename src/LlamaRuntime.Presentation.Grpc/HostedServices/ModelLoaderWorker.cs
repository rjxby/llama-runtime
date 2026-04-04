using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;

namespace LlamaRuntime.Presentation.Grpc.HostedServices;

public class ModelLoaderWorker : IHostedService
{
    private readonly ILogger<ModelLoaderWorker> _logger;
    private readonly ILlamaProvider _provider;
    private readonly IHostedModelStore _hostedModelStore;
    private readonly string _hostedModelPath;
    private readonly InferenceOptions _inferenceOptions;

    public ModelLoaderWorker(
        ILogger<ModelLoaderWorker> logger,
        ILlamaProvider provider,
        IHostedModelStore hostedModelStore,
        IOptions<HostedModelOptions> options,
        IOptions<InferenceOptions> inferenceOptions)
    {
        _logger = logger;
        _provider = provider;
        _hostedModelStore = hostedModelStore;
        _hostedModelPath = options.Value.ModelPath;
        _inferenceOptions = inferenceOptions.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ModelLoaderWorker starting. Loading model {Path}", _hostedModelPath);
        _hostedModelStore.SetLoading();

        if (string.IsNullOrEmpty(_hostedModelPath))
        {
            _logger.LogError("ModelPath not configured");
            _hostedModelStore.SetFailed(new InvalidOperationException("Model path must be configured"));
            throw new InvalidOperationException($"Model path must be configured");
        }

        try
        {
            var model = await _provider.LoadModelAsync(_hostedModelPath, cancellationToken).ConfigureAwait(false);
            _hostedModelStore.SetLoaded(model);

            if (_inferenceOptions.EnableStartupWarmup)
            {
                try
                {
                    var warmup = await _provider.InferAsync(model, _inferenceOptions.StartupWarmupPrompt, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Warm-up inference completed (len={Len})", warmup?.Length ?? 0);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Warm-up inference failed (continuing)");
                }
            }
        }
        catch (Exception ex)
        {
            _hostedModelStore.SetFailed(ex);
            _logger.LogError(ex, "Failed to load model during startup");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var snapshot = _hostedModelStore.GetSnapshot();
        _hostedModelStore.SetStopping();

        if (snapshot.Model != null)
        {
            await _provider.UnloadModelAsync(snapshot.Model, cancellationToken).ConfigureAwait(false);
        }

        _hostedModelStore.Reset();
    }
}

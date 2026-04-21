using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.HostedServices;

public class ModelLoaderWorker : IHostedService
{
    private readonly ILogger<ModelLoaderWorker> _logger;
    private readonly ILlamaProvider _provider;
    private readonly IHostedModelStateReader _hostedModelReader;
    private readonly IHostedModelStateWriter _hostedModelWriter;
    private readonly string _hostedModelPath;
    private readonly string _startupWarmupPrompt;

    public ModelLoaderWorker(
        ILogger<ModelLoaderWorker> logger,
        ILlamaProvider provider,
        IHostedModelStateReader hostedModelReader,
        IHostedModelStateWriter hostedModelWriter,
        IOptions<HostedModelOptions> options,
        IOptions<InferenceOptions> inferenceOptions)
    {
        _logger = logger;
        _provider = provider;
        _hostedModelReader = hostedModelReader;
        _hostedModelWriter = hostedModelWriter;
        _hostedModelPath = options.Value.ModelPath;
        _startupWarmupPrompt = inferenceOptions.Value.StartupWarmupPrompt;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ModelLoaderWorker starting. Loading model {Path}", _hostedModelPath);
        _hostedModelWriter.SetLoading();

        if (string.IsNullOrEmpty(_hostedModelPath))
        {
            _logger.LogError("ModelPath not configured");
            _hostedModelWriter.SetFailed(new InvalidOperationException("Model path must be configured"));
            throw new InvalidOperationException($"Model path must be configured");
        }

        IEngineModel? model = null;
        try
        {
            model = await _provider.LoadModelAsync(_hostedModelPath, cancellationToken).ConfigureAwait(false);
            _hostedModelWriter.SetWarmingUp(model);

            var warmup = await _provider.InferAsync(model, _startupWarmupPrompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Warm-up inference completed (len={Len})", warmup?.Length ?? 0);

            _hostedModelWriter.SetLoaded(model);
        }
        catch (Exception ex)
        {
            if (model != null)
            {
                try
                {
                    await _provider.UnloadModelAsync(model, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception unloadEx)
                {
                    _logger.LogWarning(unloadEx, "Failed unloading model after startup failure");
                }
            }

            _hostedModelWriter.SetFailed(ex);
            _logger.LogError(ex, "Failed to load model during startup");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var snapshot = _hostedModelReader.GetSnapshot();
        _hostedModelWriter.SetStopping();

        if (snapshot.Model != null)
        {
            await _provider.UnloadModelAsync(snapshot.Model, cancellationToken).ConfigureAwait(false);
        }

        _hostedModelWriter.Reset();
    }
}

using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;

namespace LlamaRuntime.Presentation.Grpc.HostedServices;

public class ModelLoaderWorker : IHostedService
{
    private readonly ILogger<ModelLoaderWorker> _logger;
    private readonly ILlamaProvider _provider;
    private readonly ILoadedModelAccessor _accessor;
    private readonly string _hostedModelPath;

    public ModelLoaderWorker(
        ILogger<ModelLoaderWorker> logger,
        ILlamaProvider provider,
        ILoadedModelAccessor accessor,
        IOptions<HostedModelOptions> options)
    {
        _logger = logger;
        _provider = provider;
        _accessor = accessor;
        _hostedModelPath = options.Value.ModelPath;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ModelLoaderWorker starting. Loading model {Path}", _hostedModelPath);

        if (string.IsNullOrEmpty(_hostedModelPath))
        {
            _logger.LogError("ModelPath not configured");
            throw new InvalidOperationException($"Model path must be configured");
        }

        try
        {
            var model = await _provider.LoadModelAsync(_hostedModelPath, cancellationToken).ConfigureAwait(false);
            _accessor.Model = model;

            try
            {
                var warmup = await _provider.InferAsync(model, "Hello", cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Warm-up inference completed (len={Len})", warmup?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Warm-up inference failed (continuing)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model during startup");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

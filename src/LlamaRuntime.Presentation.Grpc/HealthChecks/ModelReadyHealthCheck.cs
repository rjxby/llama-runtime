using Microsoft.Extensions.Diagnostics.HealthChecks;
using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.HealthChecks;

public class ModelReadyHealthCheck : IHealthCheck
{
    private readonly IHostedModelStore _hostedModelStore;

    public ModelReadyHealthCheck(IHostedModelStore hostedModelStore) => _hostedModelStore = hostedModelStore;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = _hostedModelStore.GetSnapshot();
        var result = snapshot.State switch
        {
            HostedModelState.Loaded => HealthCheckResult.Healthy("Model loaded and ready."),
            HostedModelState.Loading => HealthCheckResult.Degraded("Model is loading."),
            HostedModelState.Failed => HealthCheckResult.Unhealthy(snapshot.FailureMessage ?? "Model failed to load."),
            HostedModelState.Stopping => HealthCheckResult.Unhealthy("Model is stopping."),
            _ => HealthCheckResult.Unhealthy("Model not loaded.")
        };

        return Task.FromResult(result);
    }
}

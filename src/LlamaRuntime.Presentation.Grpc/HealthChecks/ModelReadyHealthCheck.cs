using Microsoft.Extensions.Diagnostics.HealthChecks;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.HealthChecks;

public class ModelReadyHealthCheck : IHealthCheck
{
    private readonly IHostedModel _hostedModel;

    public ModelReadyHealthCheck(IHostedModel hostedModel) => _hostedModel = hostedModel;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = _hostedModel.GetSnapshot();
        var result = snapshot.State switch
        {
            HostedModelState.Loaded => HealthCheckResult.Healthy("Model loaded and ready."),
            HostedModelState.Loading => HealthCheckResult.Degraded("Model is loading."),
            HostedModelState.WarmingUp => HealthCheckResult.Degraded("Model is warming up."),
            HostedModelState.Failed => HealthCheckResult.Unhealthy(snapshot.FailureMessage ?? "Model failed to load."),
            HostedModelState.Stopping => HealthCheckResult.Unhealthy("Model is stopping."),
            _ => HealthCheckResult.Unhealthy("Model not loaded.")
        };

        return Task.FromResult(result);
    }
}

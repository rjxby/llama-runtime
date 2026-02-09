using Microsoft.Extensions.Diagnostics.HealthChecks;
using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.HealthChecks;

public class ModelReadyHealthCheck : IHealthCheck
{
    private readonly ILoadedModelAccessor _accessor;

    public ModelReadyHealthCheck(ILoadedModelAccessor accessor) => _accessor = accessor;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_accessor.Model != null) return Task.FromResult(HealthCheckResult.Healthy("Model loaded"));
        return Task.FromResult(HealthCheckResult.Unhealthy("Model not loaded"));
    }
}

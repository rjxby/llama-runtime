using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

using LlamaRuntime.Presentation.Grpc.HealthChecks;

namespace LlamaRuntime.Presentation.Grpc.Configuration;

public static class HealthCheckEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLlamaHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Name == LlamaHealthChecks.ModelReady
        });

        return endpoints;
    }
}

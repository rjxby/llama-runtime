using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using LlamaRuntime.Engine;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native;
using LlamaRuntime.Presentation.Grpc.Auth;
using LlamaRuntime.Presentation.Grpc.HealthChecks;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.Services;

namespace LlamaRuntime.Presentation.Grpc.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlamaCore(this IServiceCollection services)
    {
        services.AddLlamaNative();
        services.AddLlamaProvider();
        services.AddSingleton<IHostedModelStore, HostedModelStore>();
        services.AddHostedService<ModelLoaderWorker>();
        services.AddSingleton<QueuedInferenceExecutor>();
        services.AddSingleton<IInferenceExecutor>(sp => sp.GetRequiredService<QueuedInferenceExecutor>());
        services.AddHostedService(sp => sp.GetRequiredService<QueuedInferenceExecutor>());

        services.AddOptions<InferenceOptions>()
                .BindConfiguration(InferenceOptions.SectionName)
                .Validate(o => o.ChannelCapacity > 0, $"{nameof(InferenceOptions.ChannelCapacity)} must be greater than zero")
                .Validate(o => o.WorkerCount > 0, $"{nameof(InferenceOptions.WorkerCount)} must be greater than zero")
                .Validate(o => o.AcquireTimeout > TimeSpan.Zero, $"{nameof(InferenceOptions.AcquireTimeout)} must be greater than zero")
                .ValidateOnStart();

        return services;
    }

    public static IServiceCollection AddHostedModel(this IServiceCollection services)
    {
        services.AddOptions<HostedModelOptions>()
                .BindConfiguration(HostedModelOptions.SectionName)
                .Validate(o => !string.IsNullOrWhiteSpace(o.ModelPath),
                          $"{nameof(HostedModelOptions.ModelPath)} must be set")
                .ValidateOnStart();

        return services;
    }

    public static IServiceCollection AddApiKeyAuth(this IServiceCollection services)
    {
        services.AddOptions<ApiKeyOptions>()
                .BindConfiguration(ApiKeyOptions.SectionName);

        services.AddSingleton<IApiKeyValidator, InMemoryApiKeyValidator>();

        services
            .AddAuthentication(AuthConstants.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                AuthConstants.AuthenticationScheme,
                _ => { });
        services.AddAuthorization();
        return services;
    }

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var rateLimiterOptions = config.GetSection(RateLimiterOptions.SectionName).Get<RateLimiterOptions>() ?? new RateLimiterOptions();
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpCtx =>
            {
                var key = httpCtx.Request.Headers[rateLimiterOptions.ApiKeyHeaderName].FirstOrDefault() ?? "anonymous";

                var limiterOptions = new TokenBucketRateLimiterOptions
                {
                    TokenLimit = rateLimiterOptions.TokenLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = rateLimiterOptions.QueueLimit,
                    ReplenishmentPeriod = rateLimiterOptions.ReplenishmentPeriod,
                    TokensPerPeriod = rateLimiterOptions.TokensPerPeriod,
                    AutoReplenishment = true
                };

                return RateLimitPartition.GetTokenBucketLimiter(key, _ => limiterOptions);
            });

            options.RejectionStatusCode = rateLimiterOptions.RejectionStatusCode;
        });

        return services;
    }

    public static IServiceCollection AddLlamaHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
                .AddCheck<ModelReadyHealthCheck>("model_ready");
        return services;
    }
}

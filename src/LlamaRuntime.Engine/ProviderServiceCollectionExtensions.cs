using Microsoft.Extensions.DependencyInjection;

using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Engine;

public static class ProviderServiceCollectionExtensions
{
    public static IServiceCollection AddLlamaProvider(this IServiceCollection services)
    {
        services.AddSingleton<ILlamaProvider, LlamaProvider>();

        return services;
    }
}

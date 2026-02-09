using Microsoft.Extensions.DependencyInjection;

using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;

namespace LlamaRuntime.Native;

public static class NativeServiceCollectionExtensions
{
    public static IServiceCollection AddLlamaNative(this IServiceCollection services)
    {
        services.AddOptions<LlamaNativeOptions>()
                .BindConfiguration(LlamaNativeOptions.SectionName)
                .Validate(o => !string.IsNullOrWhiteSpace(o.NativeLibraryPath),
                          $"{nameof(LlamaNativeOptions.NativeLibraryPath)} must be set")
                .ValidateOnStart();

        services.AddSingleton<ILlamaNative, LlamaNative>();

        return services;
    }
}

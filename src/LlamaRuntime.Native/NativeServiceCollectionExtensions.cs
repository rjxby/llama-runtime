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
                .Validate(o => o.ContextSize >= 1 && o.ContextSize <= 65_536,
                          $"{nameof(LlamaNativeOptions.ContextSize)} must be between 1 and 65536")
                .Validate(o => o.BatchSize >= 1 && o.BatchSize <= 4_096,
                          $"{nameof(LlamaNativeOptions.BatchSize)} must be between 1 and 4096")
                .Validate(o => o.BatchSize <= o.ContextSize,
                          $"{nameof(LlamaNativeOptions.BatchSize)} must be less than or equal to {nameof(LlamaNativeOptions.ContextSize)}")
                .Validate(o => o.MaxTokens >= 1 && o.MaxTokens <= 262_144,
                          $"{nameof(LlamaNativeOptions.MaxTokens)} must be between 1 and 262144")
                .Validate(o => o.GenerationMaxNewTokens >= 1 && o.GenerationMaxNewTokens <= 16_384,
                          $"{nameof(LlamaNativeOptions.GenerationMaxNewTokens)} must be between 1 and 16384")
                .Validate(o => o.GenerationMaxNewTokens < o.ContextSize,
                          $"{nameof(LlamaNativeOptions.GenerationMaxNewTokens)} must be less than {nameof(LlamaNativeOptions.ContextSize)}")
                .Validate(o => o.InferenceBufferSize >= 1 && o.InferenceBufferSize <= 16_777_216,
                          $"{nameof(LlamaNativeOptions.InferenceBufferSize)} must be between 1 and 16777216")
                .ValidateOnStart();

        services.AddSingleton<ILlamaNative, LlamaNative>();

        return services;
    }
}

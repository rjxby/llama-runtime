using Microsoft.Extensions.Configuration;

namespace LlamaRuntime.Benchmarks.Configuration;

public static class ConfigurationLoader
{
    public static BenchmarkOptions Load()
    {
        var builder = new ConfigurationBuilder()
            .AddEnvironmentVariables("BENCH_");

        var config = builder.Build();
        var options = new BenchmarkOptions();
        config.Bind(options);

        Validate(options);
        return options;
    }

    private static void Validate(BenchmarkOptions options)
    {
        if (options.Mode == BenchmarkMode.LlamaRuntimeGrpc)
        {
            if (string.IsNullOrEmpty(options.ApiKey))
            {
                throw new InvalidOperationException("ApiKey is required for Llama Runtime Grpc mode. Set BENCH_APIKEY.");
            }

            if (!Uri.TryCreate(options.GrpcUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("GrpcUrl must be an absolute URI. Set BENCH_GRPCURL.");
            }
        }
        else if (options.Mode == BenchmarkMode.LlamaRest)
        {
            if (string.IsNullOrEmpty(options.LlamaRestUrl))
            {
                 throw new InvalidOperationException("LlamaRestUrl is required for LlamaRest mode. Set BENCH_LLAMARESTURL.");
            }
        }

        if (options.Iterations <= 0) throw new InvalidOperationException("Iterations must be > 0");
        if (options.Concurrency <= 0) throw new InvalidOperationException("Concurrency must be > 0");
        if (options.LlamaRestMaxNewTokens <= 0) throw new InvalidOperationException("LlamaRestMaxNewTokens must be > 0");
        if (options.LlamaRestTemperature < 0) throw new InvalidOperationException("LlamaRestTemperature must be >= 0");
    }
}

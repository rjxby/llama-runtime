using Grpc.Net.Client;
using LlamaRuntime.Benchmarks.Common;
using LlamaRuntime.Presentation.Grpc;
using LlamaRuntime.Presentation.Grpc.Auth;
using LlamaRuntime.Benchmarks.Configuration;

public static class GrpcBenchmark
{
    public static async Task<BenchmarkResult> RunAsync(BenchmarkOptions options)
    {
        Console.WriteLine("=== gRPC benchmark (out-of-process) ===");

        // API Key is already validated by ConfigurationLoader
        var apiKey = options.ApiKey!;

        var httpClient = new HttpClient
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        httpClient.DefaultRequestHeaders.Add(
            AuthConstants.AuthenticationScheme,
            apiKey);

        using var channel = GrpcChannel.ForAddress(
            "http://localhost:5000",
            new GrpcChannelOptions
            {
                HttpClient = httpClient
            });

        var client = new Generator.GeneratorClient(channel);

        // Warmup
        Console.WriteLine("Warming up...");
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await client.GenerateAsync(new GenerateRequest
                {
                    RequestId = $"warmup-{i}",
                    Prompt = options.Prompt
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warmup failed: {ex.Message}");
            }
        }
        Console.WriteLine("Warmup done.\n");

        return await BenchmarkRunner.RunAsync(
            options.Iterations,
            options.Concurrency,
            async idx =>
            {
                var reply = await client.GenerateAsync(new GenerateRequest
                {
                    RequestId = $"run-{idx}",
                    Prompt = options.Prompt
                });

                return !string.IsNullOrEmpty(reply.Result);
            });
    }
}

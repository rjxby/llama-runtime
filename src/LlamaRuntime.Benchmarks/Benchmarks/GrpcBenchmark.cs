using Grpc.Net.Client;
using LlamaRuntime.Benchmarks.Common;
using LlamaRuntime.Presentation.Grpc;
using LlamaRuntime.Presentation.Grpc.Auth;
using LlamaRuntime.Benchmarks.Configuration;
using Microsoft.Extensions.Logging;

public static class GrpcBenchmark
{
    public static async Task<BenchmarkResult> RunAsync(BenchmarkOptions options, ILogger logger)
    {
        logger.LogInformation("=== gRPC benchmark (out-of-process) ===");

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
            options.GrpcUrl,
            new GrpcChannelOptions
            {
                HttpClient = httpClient
            });

        var client = new Generator.GeneratorClient(channel);

        // Warmup
        logger.LogInformation("Warming up...");
        for (int i = 0; i < 5; i++)
        {
            var requestId = $"warmup-{i}";
            try
            {
                var reply = await client.GenerateAsync(new GenerateRequest
                {
                    RequestId = requestId,
                    Prompt = options.Prompt
                }).ConfigureAwait(false);

                var validation = BenchmarkResponseValidator.ValidateGeneratedText(
                    reply.Result,
                    options.StrictResponseValidation,
                    "gRPC");

                if (!validation.Success)
                {
                    logger.LogWarning(
                        "Warmup response validation failed for {RequestId}: {FailureReason}",
                        requestId,
                        validation.FailureReason);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Warmup failed for {RequestId}", requestId);
            }
        }
        logger.LogInformation("Warmup done.");

        return await BenchmarkRunner.RunAsync(
            options.Iterations,
            options.Concurrency,
            async idx =>
            {
                var requestId = $"run-{idx}";
                var reply = await client.GenerateAsync(new GenerateRequest
                {
                    RequestId = requestId,
                    Prompt = options.Prompt
                }).ConfigureAwait(false);

                return BenchmarkResponseValidator.ValidateGeneratedText(
                    reply.Result,
                    options.StrictResponseValidation,
                    "gRPC");
            },
            logger).ConfigureAwait(false);
    }
}

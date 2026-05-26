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
                var reply = await client.GenerateAsync(CreateRequest(requestId, options)).ConfigureAwait(false);

                var validation = BenchmarkResponseValidator.ValidateGeneratedText(
                    reply.Content,
                    options.StrictResponseValidation,
                    "gRPC",
                    options.ResponseFormatKind,
                    requestId,
                    options.ResponseFormatKind == BenchmarkResponseFormat.Json ? BenchmarkJsonSchema.SchemaJson : null);

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
                var reply = await client.GenerateAsync(CreateRequest(requestId, options)).ConfigureAwait(false);

                return BenchmarkResponseValidator.ValidateGeneratedText(
                    reply.Content,
                    options.StrictResponseValidation,
                    "gRPC",
                    options.ResponseFormatKind,
                    requestId,
                    options.ResponseFormatKind == BenchmarkResponseFormat.Json ? BenchmarkJsonSchema.SchemaJson : null);
            },
            new BenchmarkRunMetadata(
                options.Mode,
                options.Prompt,
                options.ResponseFormatKind,
                options.LogInvocations ? options.InvocationFile : null),
            logger).ConfigureAwait(false);
    }

    private static GenerateRequest CreateRequest(string requestId, BenchmarkOptions options)
    {
        var request = new GenerateRequest
        {
            RequestId = requestId,
            Prompt = options.Prompt
        };

        if (options.ResponseFormatKind == BenchmarkResponseFormat.Json)
        {
            request.ResponseFormat = new ResponseFormat
            {
                Type = "json",
                JsonSchema = BenchmarkJsonSchema.SchemaJson
            };
        }

        return request;
    }
}

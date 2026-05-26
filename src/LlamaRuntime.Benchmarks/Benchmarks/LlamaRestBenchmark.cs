using System.Net.Http.Json;
using System.Text.Json;
using LlamaRuntime.Benchmarks.Common;
using LlamaRuntime.Benchmarks.Configuration;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Benchmarks;

public static class LlamaRestBenchmark
{
    public static async Task<BenchmarkResult> RunAsync(BenchmarkOptions options, ILogger logger)
    {
        logger.LogInformation("=== llama.cpp REST benchmark ===");
        var url = options.LlamaRestUrl!;
        var maxNewTokens = options.LlamaRestMaxNewTokens;
        var temperature = options.LlamaRestTemperature;
        logger.LogInformation("Endpoint: {Endpoint}", url);
        logger.LogInformation(
            "REST decode settings: n_predict={MaxNewTokens}, temperature={Temperature}, response_format={ResponseFormat}",
            maxNewTokens,
            temperature,
            BenchmarkResponseFormatParser.ToWireValue(options.ResponseFormatKind));

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromMinutes(5)
        };

        var payload = CreatePayload(options);

        logger.LogInformation("Warming up...");
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await SendRequestAsync(
                    httpClient,
                    payload,
                    options,
                    logger,
                    $"warmup-{i}",
                    isWarmup: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Warmup failed for warmup-{Iteration}", i);
            }
        }

        logger.LogInformation("Warmup done.");

        return await BenchmarkRunner.RunAsync(
            options.Iterations,
            options.Concurrency,
            idx => SendRequestAsync(
                httpClient,
                payload,
                options,
                logger,
                $"run-{idx}",
                isWarmup: false),
            new BenchmarkRunMetadata(
                options.Mode,
                options.Prompt,
                options.ResponseFormatKind,
                options.LogInvocations ? options.InvocationFile : null),
            logger).ConfigureAwait(false);
    }

    private static object CreatePayload(BenchmarkOptions options)
    {
        if (options.ResponseFormatKind == BenchmarkResponseFormat.Json)
        {
            return new
            {
                prompt = options.Prompt,
                n_predict = options.LlamaRestMaxNewTokens,
                temperature = options.LlamaRestTemperature,
                json_schema = JsonSerializer.Deserialize<JsonElement>(BenchmarkJsonSchema.SchemaJson)
            };
        }

        return new
        {
            prompt = options.Prompt,
            n_predict = options.LlamaRestMaxNewTokens,
            temperature = options.LlamaRestTemperature
        };
    }

    private static async Task<BenchmarkInvocationResult> SendRequestAsync(
        HttpClient httpClient,
        object payload,
        BenchmarkOptions options,
        ILogger logger,
        string requestId,
        bool isWarmup)
    {
        using var response = await httpClient.PostAsJsonAsync("", payload).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            response.EnsureSuccessStatusCode();
        }

        var (result, parseResult) = BenchmarkResponseValidator.ValidateLlamaRestResponse(
            responseBody,
            options.StrictResponseValidation,
            options.ResponseFormatKind,
            requestId,
            options.ResponseFormatKind == BenchmarkResponseFormat.Json ? BenchmarkJsonSchema.SchemaJson : null);

        if (isWarmup && !result.Success)
        {
            logger.LogWarning(
                "Warmup response validation failed for {RequestId}: {FailureReason}",
                requestId,
                result.FailureReason ?? parseResult.FailureReason ?? "Unknown failure");
        }

        return result;
    }
}

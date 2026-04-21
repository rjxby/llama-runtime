using System.Net.Http.Json;
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
        logger.LogInformation("Endpoint: {Endpoint}", url);

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromMinutes(5)
        };

        var payload = new
        {
            prompt = options.Prompt,
            n_predict = 128,
            temperature = 0.8
        };

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
            logger).ConfigureAwait(false);
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
            options.StrictResponseValidation);

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

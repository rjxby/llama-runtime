using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Benchmarks.Common;

internal static class BenchmarkWarmupRunner
{
    private const int WarmupIterations = 5;

    public static async Task RunAsync(
        Func<string, Task<BenchmarkInvocationResult>> invokeAsync,
        ILogger logger)
    {
        logger.LogInformation("Warming up...");
        for (int i = 0; i < WarmupIterations; i++)
        {
            var requestId = $"warmup-{i}";
            try
            {
                var validation = await invokeAsync(requestId).ConfigureAwait(false);
                if (!validation.Success)
                {
                    logger.LogWarning(
                        "Warmup response validation failed for {RequestId}: {FailureReason}",
                        requestId,
                        validation.FailureReason ?? validation.RestParseFailureReason ?? "Unknown failure");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Warmup failed for {RequestId}", requestId);
            }
        }

        logger.LogInformation("Warmup done.");
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Benchmarks.Common;

public static class BenchmarkRunner
{
    public static async Task<BenchmarkResult> RunAsync(
        int iterations,
        int concurrency,
        Func<int, Task<BenchmarkInvocationResult>> invokeAsync,
        BenchmarkRunMetadata metadata,
        ILogger logger)
    {
        var timings = new List<long>();
        int successCount = 0;
        int errorCount = 0;
        object lockObj = new();
        using var invocationWriter = string.IsNullOrWhiteSpace(metadata.InvocationFile)
            ? null
            : new BenchmarkInvocationJsonlWriter(metadata.InvocationFile);

        async Task RunOnce(int idx)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await invokeAsync(idx).ConfigureAwait(false);
                sw.Stop();
                var elapsedMs = sw.ElapsedMilliseconds;

                lock (lockObj)
                {
                    if (result.Success)
                    {
                        timings.Add(elapsedMs);
                        successCount++;
                    }
                    else
                    {
                         errorCount++;
                    }
                }

                WriteInvocationRecord(invocationWriter, metadata, idx, elapsedMs, result);

                if (result.Success)
                {
                    logger.LogInformation(
                        "Run {Iteration} completed in {ElapsedMs} ms with status OK",
                        idx,
                        elapsedMs);
                }
                else
                {
                    logger.LogWarning(
                        "Run {Iteration} completed in {ElapsedMs} ms with status ERR: {FailureReason}",
                        idx,
                        elapsedMs,
                        result.FailureReason ?? "Unknown failure");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                var elapsedMs = sw.ElapsedMilliseconds;

                lock (lockObj)
                {
                    errorCount++;
                }

                WriteInvocationRecord(
                    invocationWriter,
                    metadata,
                    idx,
                    elapsedMs,
                    new BenchmarkInvocationResult(false, ex.Message, $"run-{idx}"));

                logger.LogError(ex, "Run {Iteration} failed with an exception", idx);
            }
        }

        logger.LogInformation("Running benchmark...");
        var total = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i += concurrency)
        {
            var batch = Enumerable
                .Range(i, Math.Min(concurrency, iterations - i))
                .Select(RunOnce);

            await Task.WhenAll(batch);
        }

        total.Stop();

        if (timings.Count == 0)
        {
            logger.LogInformation(
                "Benchmark completed with no successful runs. Success={SuccessCount} Errors={ErrorCount}",
                successCount,
                errorCount);

            return new BenchmarkResult(total.ElapsedMilliseconds, 0, 0, 0, 0, 0, successCount, errorCount);
        }

        timings.Sort();

        static double P(List<long> d, double p) => d[(int)(d.Count * p)];

        var avg = timings.Average();
        var p50 = P(timings, 0.50);
        var p90 = P(timings, 0.90);
        var p99 = P(timings, 0.99);
        var throughput = iterations * 1000.0 / total.ElapsedMilliseconds;

        logger.LogInformation("=== Results ===");
        logger.LogInformation("Total wall time : {TotalTimeMs} ms", total.ElapsedMilliseconds);
        logger.LogInformation("Avg latency     : {AvgLatencyMs:F1} ms", avg);
        logger.LogInformation("P50 latency     : {P50} ms", p50);
        logger.LogInformation("P90 latency     : {P90} ms", p90);
        logger.LogInformation("P99 latency     : {P99} ms", p99);
        logger.LogInformation("Throughput      : {ThroughputRps:F2} req/s", throughput);
        logger.LogInformation("Success         : {SuccessCount}", successCount);
        logger.LogInformation("Errors          : {ErrorCount}", errorCount);

        return new BenchmarkResult(
            total.ElapsedMilliseconds,
            avg,
            p50,
            p90,
            p99,
            throughput,
            successCount,
            errorCount
        );
    }

    private static void WriteInvocationRecord(
        BenchmarkInvocationJsonlWriter? writer,
        BenchmarkRunMetadata metadata,
        int iteration,
        long latencyMs,
        BenchmarkInvocationResult result)
    {
        if (writer is null)
        {
            return;
        }

        writer.Write(new BenchmarkInvocationLogRecord(
            DateTimeOffset.UtcNow,
            metadata.Mode,
            iteration,
            result.RequestId ?? $"run-{iteration}",
            metadata.ResponseFormat,
            latencyMs,
            result.Success,
            result.FailureReason,
            metadata.Prompt,
            result.Output,
            result.RestParseFailureReason));
    }
}

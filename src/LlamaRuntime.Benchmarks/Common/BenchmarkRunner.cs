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
        int diagnosticErrorCount = 0;
        BenchmarkInvocationJsonlWriter? invocationWriter = null;

        void TryDiagnostic(Action diagnostic)
        {
            try
            {
                diagnostic();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref diagnosticErrorCount);
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata.InvocationFile))
        {
            TryDiagnostic(() => invocationWriter = new BenchmarkInvocationJsonlWriter(metadata.InvocationFile));
        }

        async Task RunOnce(int idx)
        {
            var sw = Stopwatch.StartNew();
            BenchmarkInvocationResult result;
            Exception? invocationFailure = null;
            try
            {
                result = await invokeAsync(idx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                invocationFailure = ex;
                result = new BenchmarkInvocationResult(false, ex.Message, $"run-{idx}");
            }
            finally
            {
                sw.Stop();
            }

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

            TryDiagnostic(() => WriteInvocationRecord(invocationWriter, metadata, idx, elapsedMs, result));
            TryDiagnostic(() =>
            {
                if (invocationFailure != null)
                {
                    logger.LogError(invocationFailure, "Run {Iteration} failed with an exception", idx);
                }
                else if (result.Success)
                {
                    logger.LogInformation(
                        "Run {Iteration} completed in {ElapsedMs} ms with status OK", idx, elapsedMs);
                }
                else
                {
                    logger.LogWarning(
                        "Run {Iteration} completed in {ElapsedMs} ms with status ERR: {FailureReason}",
                        idx, elapsedMs, result.FailureReason ?? "Unknown failure");
                }
            });
        }

        var total = new Stopwatch();
        try
        {
            TryDiagnostic(() => logger.LogInformation("Running benchmark..."));
            total.Start();
            for (int i = 0; i < iterations; i += concurrency)
            {
                var batch = Enumerable
                    .Range(i, Math.Min(concurrency, iterations - i))
                    .Select(RunOnce);

                await Task.WhenAll(batch);
            }
        }
        finally
        {
            total.Stop();
            TryDiagnostic(() => invocationWriter?.Dispose());
        }

        BenchmarkResult benchmarkResult;
        if (timings.Count == 0)
        {
            TryDiagnostic(() => logger.LogInformation(
                "Benchmark completed with no successful runs. Success={SuccessCount} Errors={ErrorCount}",
                successCount, errorCount));
            benchmarkResult = new BenchmarkResult(total.ElapsedMilliseconds, 0, 0, 0, 0, 0, successCount, errorCount);
        }
        else
        {
            timings.Sort();
            static double P(List<long> d, double p) => d[(int)(d.Count * p)];
            var avg = timings.Average();
            var p50 = P(timings, 0.50);
            var p90 = P(timings, 0.90);
            var p99 = P(timings, 0.99);
            var throughput = iterations * 1000.0 / total.ElapsedMilliseconds;

            TryDiagnostic(() => logger.LogInformation("=== Results ==="));
            TryDiagnostic(() => logger.LogInformation("Total wall time : {TotalTimeMs} ms", total.ElapsedMilliseconds));
            TryDiagnostic(() => logger.LogInformation("Avg latency     : {AvgLatencyMs:F1} ms", avg));
            TryDiagnostic(() => logger.LogInformation("P50 latency     : {P50} ms", p50));
            TryDiagnostic(() => logger.LogInformation("P90 latency     : {P90} ms", p90));
            TryDiagnostic(() => logger.LogInformation("P99 latency     : {P99} ms", p99));
            TryDiagnostic(() => logger.LogInformation("Throughput      : {ThroughputRps:F2} req/s", throughput));
            TryDiagnostic(() => logger.LogInformation("Success         : {SuccessCount}", successCount));
            TryDiagnostic(() => logger.LogInformation("Errors          : {ErrorCount}", errorCount));
            benchmarkResult = new BenchmarkResult(total.ElapsedMilliseconds, avg, p50, p90, p99,
                throughput, successCount, errorCount);
        }

        TryDiagnostic(() => logger.LogInformation("Diagnostic errors: {DiagnosticErrorCount}", Volatile.Read(ref diagnosticErrorCount)));
        return benchmarkResult with { DiagnosticErrorCount = Volatile.Read(ref diagnosticErrorCount) };
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

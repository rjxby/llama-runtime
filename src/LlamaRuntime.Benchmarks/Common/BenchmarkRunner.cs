using System.Diagnostics;

namespace LlamaRuntime.Benchmarks.Common;

public record BenchmarkResult(
    double TotalTimeMs,
    double AvgLatencyMs,
    double P50,
    double P90,
    double P99,
    double ThroughputRps,
    int SuccessCount,
    int ErrorCount
);

public static class BenchmarkRunner
{
    public static async Task<BenchmarkResult> RunAsync(
        int iterations,
        int concurrency,
        Func<int, Task<bool>> invokeAsync)
    {
        var timings = new List<long>();
        int successCount = 0;
        int errorCount = 0;
        object lockObj = new();

        async Task RunOnce(int idx)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var success = await invokeAsync(idx);
                sw.Stop();

                lock (lockObj)
                {
                    if (success)
                    {
                        timings.Add(sw.ElapsedMilliseconds);
                        successCount++;
                    }
                    else
                    {
                         errorCount++;
                    }
                }

                string status = success ? "OK" : "ERR";
                Console.WriteLine(
                    $"Run {idx,-3} | {sw.ElapsedMilliseconds,5} ms | {status}");
            }
            catch (Exception ex)
            {
                lock (lockObj)
                {
                    errorCount++;
                }
                Console.WriteLine($"Run {idx,-3} | ERROR: {ex.Message}");
            }
        }

        Console.WriteLine("Running benchmark...");
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
             return new BenchmarkResult(total.ElapsedMilliseconds, 0, 0, 0, 0, 0, successCount, errorCount);
        }

        timings.Sort();

        static double P(List<long> d, double p) => d[(int)(d.Count * p)];

        var avg = timings.Average();
        var p50 = P(timings, 0.50);
        var p90 = P(timings, 0.90);
        var p99 = P(timings, 0.99);
        var throughput = iterations * 1000.0 / total.ElapsedMilliseconds;

        Console.WriteLine("\n=== Results ===");
        Console.WriteLine($"Total wall time : {total.ElapsedMilliseconds} ms");
        Console.WriteLine($"Avg latency     : {avg:F1} ms");
        Console.WriteLine($"P50 latency     : {p50} ms");
        Console.WriteLine($"P90 latency     : {p90} ms");
        Console.WriteLine($"P99 latency     : {p99} ms");
        Console.WriteLine($"Throughput      : {throughput:F2} req/s");
        Console.WriteLine($"Success         : {successCount}");
        Console.WriteLine($"Errors          : {errorCount}");

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
}

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

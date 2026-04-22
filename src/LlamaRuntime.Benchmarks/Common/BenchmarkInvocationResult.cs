namespace LlamaRuntime.Benchmarks.Common;

public sealed record BenchmarkInvocationResult(
    bool Success,
    string? FailureReason = null);

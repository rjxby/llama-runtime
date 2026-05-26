namespace LlamaRuntime.Benchmarks.Common;

public sealed record BenchmarkInvocationResult(
    bool Success,
    string? FailureReason = null,
    string? RequestId = null,
    string? Output = null,
    string? RestParseFailureReason = null);

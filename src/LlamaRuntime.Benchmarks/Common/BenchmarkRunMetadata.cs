using LlamaRuntime.Benchmarks.Configuration;

namespace LlamaRuntime.Benchmarks.Common;

public sealed record BenchmarkRunMetadata(
    BenchmarkMode Mode,
    string Prompt,
    BenchmarkResponseFormat ResponseFormat,
    string? InvocationFile);

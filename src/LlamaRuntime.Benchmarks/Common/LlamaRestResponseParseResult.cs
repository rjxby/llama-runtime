namespace LlamaRuntime.Benchmarks.Common;

public sealed record LlamaRestResponseParseResult(
    bool Parsed,
    string? GeneratedText,
    string? FailureReason);

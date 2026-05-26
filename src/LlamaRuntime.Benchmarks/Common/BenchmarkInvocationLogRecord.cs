using System.Text.Json.Serialization;
using LlamaRuntime.Benchmarks.Configuration;

namespace LlamaRuntime.Benchmarks.Common;

public sealed record BenchmarkInvocationLogRecord(
    [property: JsonPropertyName("timestamp")]
    DateTimeOffset Timestamp,
    [property: JsonPropertyName("mode")]
    BenchmarkMode Mode,
    [property: JsonPropertyName("iteration")]
    int Iteration,
    [property: JsonPropertyName("request_id")]
    string RequestId,
    [property: JsonPropertyName("response_format")]
    BenchmarkResponseFormat ResponseFormat,
    [property: JsonPropertyName("latency_ms")]
    long LatencyMs,
    [property: JsonPropertyName("success")]
    bool Success,
    [property: JsonPropertyName("failure_reason")]
    string? FailureReason,
    [property: JsonPropertyName("prompt")]
    string Prompt,
    [property: JsonPropertyName("output")]
    string? Output,
    [property: JsonPropertyName("rest_parse_failure_reason")]
    string? RestParseFailureReason);

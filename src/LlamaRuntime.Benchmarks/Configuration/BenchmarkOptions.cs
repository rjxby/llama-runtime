namespace LlamaRuntime.Benchmarks.Configuration;

public class BenchmarkOptions
{
    public int Iterations { get; set; } = 50;

    public int Concurrency { get; set; } = 1;

    public string Prompt { get; set; } = "Write a short story about a llama learning distributed systems.";

    public BenchmarkMode Mode { get; set; } = BenchmarkMode.LlamaRuntimeGrpc;

    public string? LlamaRestUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? OutputFile { get; set; }
}

namespace LlamaRuntime.Benchmarks.Configuration;

public class BenchmarkOptions
{
    public int Iterations { get; set; } = 50;

    public int Concurrency { get; set; } = 1;

    public string Prompt { get; set; } = "Write a short story about a llama learning distributed systems.";

    public BenchmarkMode Mode { get; set; } = BenchmarkMode.LlamaRuntimeGrpc;

    public string GrpcUrl { get; set; } = "http://localhost:5000";

    public string? LlamaRestUrl { get; set; }

    public string? ApiKey { get; set; }

    public bool StrictResponseValidation { get; set; } = true;

    public string? OutputFile { get; set; }

    public string? InvocationFile { get; set; }

    public bool LogInvocations { get; set; } = true;

    public string ResponseFormat { get; set; } = "text";

    public BenchmarkResponseFormat ResponseFormatKind { get; set; } = BenchmarkResponseFormat.Text;

    public int LlamaRestMaxNewTokens { get; set; } = 512;

    public double LlamaRestTemperature { get; set; } = 0.0;
}

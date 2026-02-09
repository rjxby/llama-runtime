using LlamaRuntime.Benchmarks;
using LlamaRuntime.Benchmarks.Common;
using LlamaRuntime.Benchmarks.Configuration;

Console.WriteLine("=== LlamaRuntime Benchmarks ===");

BenchmarkOptions options;
try
{
    options = ConfigurationLoader.Load();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    Environment.Exit(1);
    return;
}

Console.WriteLine($"Mode         : {options.Mode}");
Console.WriteLine($"Iterations   : {options.Iterations}");
Console.WriteLine($"Concurrency  : {options.Concurrency}");
Console.WriteLine($"Prompt chars : {options.Prompt.Length}");
Console.WriteLine();

BenchmarkResult? result = null;

switch (options.Mode)
{
    case BenchmarkMode.LlamaRuntimeGrpc:
        result = await GrpcBenchmark.RunAsync(options);
        break;

    case BenchmarkMode.LlamaRest:
        result = await LlamaRestBenchmark.RunAsync(options);
        break;

    default:
        Console.Error.WriteLine($"Unknown BENCH_MODE: {options.Mode}");
        Environment.Exit(1);
        break;
}

if (result != null)
{
    var outputFile = options.OutputFile;
    if (string.IsNullOrEmpty(outputFile))
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        int counter = 1;
        do
        {
            outputFile = $"benchmark_{options.Mode}_{date}_{counter}.csv";
            counter++;
        } while (File.Exists(outputFile));
    }

    bool fileExists = File.Exists(outputFile);
    using var writer = new StreamWriter(outputFile, append: true);
    if (!fileExists)
    {
        writer.WriteLine("Timestamp,Mode,Iterations,Concurrency,AvgLatency,P50,P90,P99,Throughput,SuccessRate,ErrorCount");
    }

    double successRate = (result.SuccessCount + result.ErrorCount) > 0
        ? (double)result.SuccessCount / (result.SuccessCount + result.ErrorCount)
        : 0;

    writer.WriteLine($"{DateTime.Now:O},{options.Mode},{options.Iterations},{options.Concurrency},{result.AvgLatencyMs:F2},{result.P50:F2},{result.P90:F2},{result.P99:F2},{result.ThroughputRps:F2},{successRate:P0},{result.ErrorCount}");

    Console.WriteLine($"\nResults saved to {outputFile}");
}

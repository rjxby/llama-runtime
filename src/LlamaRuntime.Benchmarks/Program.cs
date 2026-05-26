using LlamaRuntime.Benchmarks;
using LlamaRuntime.Benchmarks.Common;
using LlamaRuntime.Benchmarks.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

var loggingConfiguration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddConfiguration(loggingConfiguration.GetSection("Logging"));
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
    builder.Services.Configure<ConsoleLoggerOptions>(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.None;
    });
});

var logger = loggerFactory.CreateLogger("Program");
logger.LogInformation("=== LlamaRuntime Benchmarks ===");

BenchmarkOptions options;
try
{
    options = ConfigurationLoader.Load();
}
catch (Exception ex)
{
    logger.LogError(ex, "Configuration error");
    Environment.Exit(1);
    return;
}

logger.LogInformation("Mode         : {Mode}", options.Mode);
logger.LogInformation("Iterations   : {Iterations}", options.Iterations);
logger.LogInformation("Concurrency  : {Concurrency}", options.Concurrency);
logger.LogInformation("Response fmt : {ResponseFormat}", BenchmarkResponseFormatParser.ToWireValue(options.ResponseFormatKind));
logger.LogInformation("Prompt chars : {PromptLength}", options.Prompt.Length);

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

    options.OutputFile = outputFile;
}

if (options.LogInvocations && string.IsNullOrEmpty(options.InvocationFile))
{
    options.InvocationFile = BenchmarkInvocationLogPath.DeriveFromSummaryFile(outputFile);
}

if (options.LogInvocations)
{
    logger.LogInformation("Invocation log: {InvocationFile}", options.InvocationFile);
}

BenchmarkResult? result = null;

switch (options.Mode)
{
    case BenchmarkMode.LlamaRuntimeGrpc:
        result = await GrpcBenchmark.RunAsync(options, loggerFactory.CreateLogger(nameof(GrpcBenchmark)));
        break;

    case BenchmarkMode.LlamaRest:
        result = await LlamaRestBenchmark.RunAsync(options, loggerFactory.CreateLogger(nameof(LlamaRestBenchmark)));
        break;

    default:
        logger.LogError("Unknown BENCH_MODE: {Mode}", options.Mode);
        Environment.Exit(1);
        break;
}

if (result != null)
{
    try
    {
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

        logger.LogInformation("Results saved to {OutputFile}", outputFile);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to persist benchmark results to {OutputFile}", outputFile);
        Environment.ExitCode = 1;
        throw;
    }
}

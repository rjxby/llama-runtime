extern alias benchmark;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlamaRuntime.Common.Tests;
using benchmark::LlamaRuntime.Benchmarks.Common;
using benchmark::LlamaRuntime.Benchmarks.Configuration;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class BenchmarkRunnerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvocationLoggingFailure_DoesNotChangeAccountingOrStopMeasurements(int outcome)
    {
        var calls = 0;
        var logger = new TestLogger(message => outcome == 2
            ? message.StartsWith("Run ", StringComparison.Ordinal)
            : message.Contains("completed in", StringComparison.Ordinal));
        var result = await BenchmarkRunner.RunAsync(6, 2, _ =>
        {
            Interlocked.Increment(ref calls);
            return Invoke(outcome);
        }, Metadata(), logger);

        Assert.Equal(6, result.DiagnosticErrorCount);
        Assert.Equal(6, calls);
        Assert.Equal(outcome == 0 ? 6 : 0, result.SuccessCount);
        Assert.Equal(outcome == 0 ? 0 : 6, result.ErrorCount);
    }

    [Fact]
    public async Task InvocationWriterInitializationFailure_DoesNotStopMeasurements()
    {
        var directory = Directory.CreateTempSubdirectory("benchmark-tests-");
        try
        {
            var calls = 0;
            var result = await BenchmarkRunner.RunAsync(4, 2, _ =>
            {
                Interlocked.Increment(ref calls);
                return Invoke(0);
            }, Metadata(directory.FullName), new TestLogger());

            Assert.Equal(1, result.DiagnosticErrorCount);
            Assert.Equal(4, calls);
            Assert.Equal(4, result.SuccessCount);
            Assert.Equal(0, result.ErrorCount);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentInvocations_AreCountedAndWrittenExactlyOnce(bool loggerThrows)
    {
        var directory = Directory.CreateTempSubdirectory("benchmark-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, "invocations.jsonl");
            var calls = new int[30];
            var logger = new TestLogger(message => loggerThrows && message.StartsWith("Run ", StringComparison.Ordinal));
            var result = await BenchmarkRunner.RunAsync(calls.Length, 4, async index =>
            {
                Interlocked.Increment(ref calls[index]);
                await Task.Yield();
                return await Invoke(index % 3);
            }, Metadata(path), logger);

            Assert.Equal(loggerThrows ? calls.Length : 0, result.DiagnosticErrorCount);
            Assert.All(calls, count => Assert.Equal(1, count));
            Assert.Equal(10, result.SuccessCount);
            Assert.Equal(20, result.ErrorCount);
            var rows = File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<BenchmarkInvocationLogRecord>(line, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!).ToArray();
            Assert.Equal(calls.Length, rows.Length);
            Assert.Equal(Enumerable.Range(0, calls.Length), rows.Select(row => row.Iteration).Order());
            Assert.Equal(10, rows.Count(row => row.Success));
            Assert.All(rows.Where(row => !row.Success), row => Assert.NotNull(row.FailureReason));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryFailedDiagnostic_IsCountedOnceIncludingSummaryLogging(bool allInvocationsFail)
    {
        var directory = Directory.CreateTempSubdirectory("benchmark-tests-");
        try
        {
            var logger = new TestLogger(_ => true);
            var result = await BenchmarkRunner.RunAsync(9, 3, index => Invoke(allInvocationsFail ? 1 : index % 3),
                Metadata(directory.FullName), logger);
            Assert.Equal(allInvocationsFail ? 0 : 3, result.SuccessCount);
            Assert.Equal(allInvocationsFail ? 9 : 6, result.ErrorCount);
            Assert.Equal(logger.Messages.Count + 1, result.DiagnosticErrorCount);
            Assert.Equal(9, logger.Messages.Count(message => message.StartsWith("Run ", StringComparison.Ordinal)));
            Assert.Contains(logger.Messages, message => message.StartsWith("Diagnostic errors:", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void DiagnosticCount_PreservesPositionalConstructionAndDeconstruction()
    {
        var result = new BenchmarkResult(1, 2, 3, 4, 5, 6, 7, 8);
        var (total, average, p50, p90, p99, throughput, success, errors) = result;
        Assert.Equal((1d, 2d, 3d, 4d, 5d, 6d, 7, 8), (total, average, p50, p90, p99, throughput, success, errors));
        Assert.Equal(0, result.DiagnosticErrorCount);
    }

    private static Task<BenchmarkInvocationResult> Invoke(int outcome) => outcome switch
    {
        0 => Task.FromResult(new BenchmarkInvocationResult(true, Output: "ok")),
        1 => Task.FromResult(new BenchmarkInvocationResult(false, "returned failure")),
        _ => throw new InvalidOperationException("invocation failed")
    };

    private static BenchmarkRunMetadata Metadata(string? path = null) =>
        new(BenchmarkMode.LlamaRuntimeGrpc, "prompt", BenchmarkResponseFormat.Text, path);

    private sealed class TestLogger(Func<string, bool>? shouldThrow = null) : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Enqueue(message);
            if (shouldThrow?.Invoke(message) == true)
            {
                throw new IOException("diagnostic failed");
            }
        }
    }
}

using Grpc.Core;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Presentation.Grpc.Logging;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed partial class LoggingInterceptorTests
{
    [Fact]
    public async Task UnaryServerHandler_CancelledRpc_LogsInformationWithoutFailureLog()
    {
        var logSink = new TestLogSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(logSink);
        });

        var interceptor = new LoggingInterceptor(loggerFactory.CreateLogger<LoggingInterceptor>());
        var context = TestServerCallContext.Create();

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.UnaryServerHandler(
                new GenerateRequest
                {
                    RequestId = "cancel-log",
                    Prompt = "cancel me"
                },
                context,
                static (_, _) => Task.FromException<GenerateReply>(
                    new RpcException(new Status(StatusCode.Cancelled, "Request cancelled.")))));

        Assert.Equal(StatusCode.Cancelled, ex.StatusCode);
        Assert.Contains(logSink.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("gRPC Request cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(logSink.Entries, entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("gRPC Request failed", StringComparison.Ordinal));
    }
}

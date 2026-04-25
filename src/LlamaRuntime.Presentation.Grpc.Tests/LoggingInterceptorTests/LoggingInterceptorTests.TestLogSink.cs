using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Presentation.Grpc.Tests;

public sealed partial class LoggingInterceptorTests
{
    private sealed partial class TestLogSink : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new TestLogger(Entries);

        public void Dispose()
        {
        }
    }
}

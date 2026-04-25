using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Presentation.Grpc.Tests;

public sealed partial class LoggingInterceptorTests
{
    private sealed partial class TestLogSink
    {
        private sealed class TestLogger : ILogger
        {
            private readonly ConcurrentQueue<LogEntry> _entries;

            public TestLogger(ConcurrentQueue<LogEntry> entries)
            {
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Common.Tests;

public sealed class TestLogSink : ILoggerProvider
{
    public ConcurrentQueue<TestLogEntry> Entries { get; } = new();

    public IEnumerable<string> Messages => Entries.Select(entry => entry.Message);

    public ILogger CreateLogger(string categoryName) => new TestLogger(Entries);

    public void Dispose()
    {
    }

    private sealed class TestLogger : ILogger
    {
        private readonly ConcurrentQueue<TestLogEntry> _entries;

        public TestLogger(ConcurrentQueue<TestLogEntry> entries)
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
            _entries.Enqueue(new TestLogEntry(logLevel, formatter(state, exception)));
        }
    }
}

public sealed record TestLogEntry(LogLevel Level, string Message);

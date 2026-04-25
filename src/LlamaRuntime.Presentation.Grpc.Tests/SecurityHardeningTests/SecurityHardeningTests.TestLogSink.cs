using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Presentation.Grpc.Tests;

public sealed partial class SecurityHardeningTests
{
    private sealed partial class TestLogSink : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IEnumerable<string> Messages => Entries.Select(entry => entry.Message);

        public ILogger CreateLogger(string categoryName) => new TestLogger(Entries);

        public void Dispose()
        {
        }
    }
}

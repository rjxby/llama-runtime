using Microsoft.Extensions.Logging;

namespace LlamaRuntime.Presentation.Grpc.Tests;

public sealed partial class SecurityHardeningTests
{
    private sealed partial class TestLogSink
    {
        public sealed record LogEntry(LogLevel Level, string Message);
    }
}

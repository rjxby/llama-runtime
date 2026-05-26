using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaRuntime.Benchmarks.Common;

public sealed class BenchmarkInvocationJsonlWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public BenchmarkInvocationJsonlWriter(string path, bool append = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Invocation log path is required.", nameof(path));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(path, append);
    }

    public void Write(BenchmarkInvocationLogRecord record)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        lock (_lock)
        {
            _writer.WriteLine(json);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}

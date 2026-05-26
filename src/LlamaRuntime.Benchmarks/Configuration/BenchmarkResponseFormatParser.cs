namespace LlamaRuntime.Benchmarks.Configuration;

public static class BenchmarkResponseFormatParser
{
    public static bool TryParse(string? value, out BenchmarkResponseFormat responseFormat)
    {
        responseFormat = BenchmarkResponseFormat.Text;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Trim() switch
        {
            var text when string.Equals(text, "text", StringComparison.OrdinalIgnoreCase) =>
                Set(BenchmarkResponseFormat.Text, out responseFormat),
            var json when string.Equals(json, "json", StringComparison.OrdinalIgnoreCase) =>
                Set(BenchmarkResponseFormat.Json, out responseFormat),
            _ => false
        };
    }

    public static string ToWireValue(BenchmarkResponseFormat responseFormat) =>
        responseFormat switch
        {
            BenchmarkResponseFormat.Text => "text",
            BenchmarkResponseFormat.Json => "json",
            _ => throw new ArgumentOutOfRangeException(nameof(responseFormat), responseFormat, null)
        };

    private static bool Set(BenchmarkResponseFormat value, out BenchmarkResponseFormat responseFormat)
    {
        responseFormat = value;
        return true;
    }
}

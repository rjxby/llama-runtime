namespace LlamaRuntime.Benchmarks.Common;

public static class BenchmarkInvocationLogPath
{
    public static string DeriveFromSummaryFile(string summaryFile)
    {
        if (string.IsNullOrWhiteSpace(summaryFile))
        {
            throw new ArgumentException("Summary file path is required.", nameof(summaryFile));
        }

        var directory = Path.GetDirectoryName(summaryFile);
        var fileName = Path.GetFileNameWithoutExtension(summaryFile);
        var invocationFile = $"{fileName}.invocations.jsonl";

        if (string.IsNullOrEmpty(directory))
        {
            return invocationFile;
        }

        return Path.Combine(directory, invocationFile);
    }
}

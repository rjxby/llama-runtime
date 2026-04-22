namespace LlamaRuntime.Benchmarks.Common;

public static class BenchmarkResponseValidator
{
    public static BenchmarkInvocationResult ValidateGeneratedText(
        string? generatedText,
        bool strictResponseValidation,
        string sourceName)
    {
        if (!strictResponseValidation)
        {
            return new BenchmarkInvocationResult(true);
        }

        return string.IsNullOrWhiteSpace(generatedText)
            ? new BenchmarkInvocationResult(false, $"{sourceName} returned empty generated text.")
            : new BenchmarkInvocationResult(true);
    }

    public static (BenchmarkInvocationResult Result, LlamaRestResponseParseResult ParseResult) ValidateLlamaRestResponse(
        string json,
        bool strictResponseValidation)
    {
        var parseResult = LlamaRestResponseParser.Parse(json);

        if (!parseResult.Parsed)
        {
            return strictResponseValidation
                ? (new BenchmarkInvocationResult(false, parseResult.FailureReason), parseResult)
                : (new BenchmarkInvocationResult(true), parseResult);
        }

        var validation = ValidateGeneratedText(parseResult.GeneratedText, strictResponseValidation, "llama.cpp REST");
        return (validation, parseResult);
    }
}

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Benchmarks.Configuration;

namespace LlamaRuntime.Benchmarks.Common;

public static class BenchmarkResponseValidator
{
    public static BenchmarkInvocationResult ValidateGeneratedText(
        string? generatedText,
        bool strictResponseValidation,
        string sourceName,
        BenchmarkResponseFormat responseFormat = BenchmarkResponseFormat.Text,
        string? requestId = null,
        string? jsonSchema = null,
        string? restParseFailureReason = null)
    {
        if (responseFormat == BenchmarkResponseFormat.Json)
        {
            var jsonValidation = ValidateJson(generatedText, sourceName, jsonSchema);
            if (jsonValidation.Success)
            {
                return new BenchmarkInvocationResult(true, RequestId: requestId, Output: generatedText, RestParseFailureReason: restParseFailureReason);
            }

            return jsonValidation with { RequestId = requestId, Output = generatedText, RestParseFailureReason = restParseFailureReason };
        }

        if (!strictResponseValidation)
        {
            return new BenchmarkInvocationResult(true, RequestId: requestId, Output: generatedText, RestParseFailureReason: restParseFailureReason);
        }

        if (string.IsNullOrWhiteSpace(generatedText))
        {
            return new BenchmarkInvocationResult(false, $"{sourceName} returned empty generated text.", requestId, generatedText, restParseFailureReason);
        }

        return new BenchmarkInvocationResult(true, RequestId: requestId, Output: generatedText, RestParseFailureReason: restParseFailureReason);
    }

    public static (BenchmarkInvocationResult Result, LlamaRestResponseParseResult ParseResult) ValidateLlamaRestResponse(
        string json,
        bool strictResponseValidation,
        BenchmarkResponseFormat responseFormat = BenchmarkResponseFormat.Text,
        string? requestId = null,
        string? jsonSchema = null)
    {
        var parseResult = LlamaRestResponseParser.Parse(json);

        if (!parseResult.Parsed)
        {
            var shouldFail = strictResponseValidation || responseFormat == BenchmarkResponseFormat.Json;
            if (shouldFail)
            {
                return (new BenchmarkInvocationResult(false, parseResult.FailureReason, requestId, RestParseFailureReason: parseResult.FailureReason), parseResult);
            }

            return (new BenchmarkInvocationResult(true, RequestId: requestId, RestParseFailureReason: parseResult.FailureReason), parseResult);
        }

        var validation = ValidateGeneratedText(
            parseResult.GeneratedText,
            strictResponseValidation,
            "llama.cpp REST",
            responseFormat,
            requestId,
            jsonSchema,
            parseResult.FailureReason);
        return (validation, parseResult);
    }

    private static BenchmarkInvocationResult ValidateJson(string? generatedText, string sourceName, string? jsonSchema)
    {
        if (string.IsNullOrWhiteSpace(generatedText))
        {
            return new BenchmarkInvocationResult(false, $"{sourceName} returned empty generated text.");
        }

        try
        {
            JsonStructuredOutput.Parse(jsonSchema).ValidateOutput(generatedText);
            return new BenchmarkInvocationResult(true);
        }
        catch (ArgumentException ex)
        {
            return new BenchmarkInvocationResult(false, $"Benchmark JSON schema is invalid: {ex.Message}");
        }
        catch (StructuredOutputException ex)
        {
            return new BenchmarkInvocationResult(false, ex.Message);
        }
    }

}

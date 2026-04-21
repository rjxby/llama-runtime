using System.Text.Json;

namespace LlamaRuntime.Benchmarks.Common;

public sealed record LlamaRestResponseParseResult(
    bool Parsed,
    string? GeneratedText,
    string? FailureReason);

public static class LlamaRestResponseParser
{
    public static LlamaRestResponseParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LlamaRestResponseParseResult(false, null, "REST response body was empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryGetGeneratedText(root, out var generatedText))
            {
                return new LlamaRestResponseParseResult(true, generatedText, null);
            }

            return new LlamaRestResponseParseResult(
                false,
                null,
                "REST response JSON did not contain a recognized generated-text field.");
        }
        catch (JsonException ex)
        {
            return new LlamaRestResponseParseResult(false, null, $"REST response was not valid JSON: {ex.Message}");
        }
    }

    private static bool TryGetGeneratedText(JsonElement element, out string? generatedText)
    {
        generatedText = null;

        if (TryGetString(element, "content", out generatedText) ||
            TryGetString(element, "response", out generatedText) ||
            TryGetString(element, "text", out generatedText) ||
            TryGetString(element, "generated_text", out generatedText) ||
            TryGetString(element, "completion", out generatedText))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (TryGetString(firstChoice, "text", out generatedText))
            {
                return true;
            }

            if (firstChoice.ValueKind == JsonValueKind.Object &&
                firstChoice.TryGetProperty("message", out var message) &&
                TryGetString(message, "content", out generatedText))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }
}

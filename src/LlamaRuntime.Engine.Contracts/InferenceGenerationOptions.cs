using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine.Contracts;

public sealed record InferenceGenerationOptions
{
    public InferenceGenerationOptions(int maxOutputTokens, float temperature, float topP)
    {
        Validate(maxOutputTokens, temperature, topP);

        MaxOutputTokens = maxOutputTokens;
        Temperature = temperature;
        TopP = topP;
    }

    public int MaxOutputTokens { get; }
    public float Temperature { get; }
    public float TopP { get; }

    private static void Validate(int maxOutputTokens, float temperature, float topP)
    {
        if (!GenerationOptionRules.HasPositiveMaxTokens(maxOutputTokens))
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), $"{nameof(MaxOutputTokens)} must be greater than 0.");
        }

        if (!GenerationOptionRules.IsValidTemperature(temperature))
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), $"{nameof(Temperature)} must be finite and greater than or equal to 0.");
        }

        if (!GenerationOptionRules.IsValidTopP(topP))
        {
            throw new ArgumentOutOfRangeException(nameof(topP), $"{nameof(TopP)} must be finite, greater than 0, and less than or equal to 1.");
        }

        if (!GenerationOptionRules.IsTopPCompatibleWithTemperature(topP, temperature))
        {
            throw new ArgumentException($"{nameof(TopP)} requires {nameof(Temperature)} to be greater than 0.", nameof(topP));
        }
    }
}

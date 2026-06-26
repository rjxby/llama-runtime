namespace LlamaRuntime.Native.Contracts;

public sealed record NativeGenerationOptions
{
    public NativeGenerationOptions(int maxNewTokens, float temperature, float topP)
    {
        Validate(maxNewTokens, temperature, topP);

        MaxNewTokens = maxNewTokens;
        Temperature = temperature;
        TopP = topP;
    }

    public int MaxNewTokens { get; }
    public float Temperature { get; }
    public float TopP { get; }

    private static void Validate(int maxNewTokens, float temperature, float topP)
    {
        if (!GenerationOptionRules.HasPositiveMaxTokens(maxNewTokens))
        {
            throw new ArgumentOutOfRangeException(nameof(maxNewTokens), "MaxNewTokens must be greater than 0.");
        }

        if (!GenerationOptionRules.IsValidTemperature(temperature))
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be finite and greater than or equal to 0.");
        }

        if (!GenerationOptionRules.IsValidTopP(topP))
        {
            throw new ArgumentOutOfRangeException(nameof(topP), "TopP must be finite, greater than 0, and less than or equal to 1.");
        }

        if (!GenerationOptionRules.IsTopPCompatibleWithTemperature(topP, temperature))
        {
            throw new ArgumentException("TopP requires Temperature to be greater than 0.", nameof(topP));
        }
    }
}

namespace LlamaRuntime.Native.Contracts;

public sealed record NativeGenerationOptions
{
    private const float DefaultTopP = 1.0f;

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
        if (maxNewTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNewTokens), "MaxNewTokens must be greater than 0.");
        }

        if (!float.IsFinite(temperature) || temperature < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be finite and greater than or equal to 0.");
        }

        if (!float.IsFinite(topP) || topP <= 0.0f || topP > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(topP), "TopP must be finite, greater than 0, and less than or equal to 1.");
        }

        if (!AreClose(topP, DefaultTopP) && temperature <= 0.0f)
        {
            throw new ArgumentException("TopP requires Temperature to be greater than 0.", nameof(topP));
        }
    }

    private static bool AreClose(float left, float right) => Math.Abs(left - right) < 0.0001f;
}

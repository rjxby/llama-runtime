namespace LlamaRuntime.Native.Contracts;

public static class GenerationOptionRules
{
    public const float DefaultTemperature = 0.0f;
    public const float DefaultTopP = 1.0f;
    public const float FloatTolerance = 0.0001f;

    public static bool HasPositiveMaxTokens(int maxTokens) => maxTokens > 0;

    public static bool IsValidTemperature(float temperature) =>
        float.IsFinite(temperature) && temperature >= 0.0f;

    public static bool IsValidTopP(float topP) =>
        float.IsFinite(topP) && topP > 0.0f && topP <= 1.0f;

    public static bool IsTopPCompatibleWithTemperature(float topP, float temperature) =>
        AreClose(topP, DefaultTopP) || temperature > 0.0f;

    public static bool AreClose(float left, float right) =>
        Math.Abs(left - right) < FloatTolerance;
}

namespace LlamaRuntime.Native.Contracts;

public sealed record NativeInferenceResult(
    string Content,
    int InputTokens,
    int OutputTokens,
    int TotalTokens);

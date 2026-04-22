namespace LlamaRuntime.Engine.Contracts;

public sealed record InferenceResult(
    string Content,
    int InputTokens,
    int OutputTokens,
    int TotalTokens);

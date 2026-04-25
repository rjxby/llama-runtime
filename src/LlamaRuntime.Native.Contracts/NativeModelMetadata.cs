namespace LlamaRuntime.Native.Contracts;

public sealed record NativeModelMetadata(
    int TrainingContextSize,
    NativeTokenizerType TokenizerType);

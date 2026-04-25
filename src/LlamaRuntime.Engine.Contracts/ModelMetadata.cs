using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine.Contracts;

public sealed record ModelMetadata(
    int ContextSize,
    NativeTokenizerType TokenizerType,
    int? TrainingContextSize = null);

using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public sealed record HostedRuntimeInfo(
    HostedModelState State,
    string PublicModelId,
    int ConfiguredContextSize,
    int EffectiveContextSize,
    int? TrainingContextSize,
    NativeTokenizerType TokenizerType,
    string TokenizerFamily,
    RuntimeCapabilityStatus StructuredOutput,
    RuntimeCapabilityStatus JsonOutput,
    RuntimeCapabilityStatus SpeculativeDecoding,
    string? FailureMessage = null);

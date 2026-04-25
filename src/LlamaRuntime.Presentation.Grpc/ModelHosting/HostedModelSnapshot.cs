using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public sealed record HostedModelSnapshot(
    HostedModelState State,
    IEngineModel? Model,
    string? FailureMessage = null,
    string? ConfiguredModelId = null);

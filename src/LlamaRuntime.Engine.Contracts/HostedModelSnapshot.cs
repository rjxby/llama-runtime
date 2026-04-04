namespace LlamaRuntime.Engine.Contracts;

public sealed record HostedModelSnapshot(
    HostedModelState State,
    IEngineModel? Model,
    string? FailureMessage = null);

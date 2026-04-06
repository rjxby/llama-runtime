namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public enum HostedModelState
{
    NotLoaded = 0,
    Loading = 1,
    WarmingUp = 2,
    Loaded = 3,
    Failed = 4,
    Stopping = 5
}

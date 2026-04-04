namespace LlamaRuntime.Engine.Contracts;

public enum HostedModelState
{
    NotLoaded = 0,
    Loading = 1,
    Loaded = 2,
    Failed = 3,
    Stopping = 4
}

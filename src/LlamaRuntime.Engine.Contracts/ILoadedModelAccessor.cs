namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// Represents a loaded model accessor.
/// </summary>
public interface ILoadedModelAccessor
{
    IEngineModel? Model { get; set; }
}

using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Engine;

public class LoadedModelAccessor : ILoadedModelAccessor
{
    public IEngineModel? Model { get; set; }
}

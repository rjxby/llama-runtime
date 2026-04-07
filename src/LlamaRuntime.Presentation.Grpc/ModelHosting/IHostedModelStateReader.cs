using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public interface IHostedModelStateReader
{
    HostedModelSnapshot GetSnapshot();
    bool TryGetLoadedModel(out IEngineModel? model);
}

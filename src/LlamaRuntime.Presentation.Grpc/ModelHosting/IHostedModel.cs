using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public interface IHostedModel
{
    HostedModelSnapshot GetSnapshot();
    bool TryGetLoadedModel(out IEngineModel? model);
    void SetLoading();
    void SetWarmingUp(IEngineModel model, string? configuredModelId = null);
    void SetLoaded(IEngineModel model, string? configuredModelId = null);
    void SetFailed(Exception exception);
    void SetStopping();
    void Reset();
}

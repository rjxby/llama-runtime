namespace LlamaRuntime.Engine.Contracts;

public interface IHostedModelStore
{
    HostedModelSnapshot GetSnapshot();
    bool TryGetLoadedModel(out IEngineModel? model);
    void SetLoading();
    void SetLoaded(IEngineModel model);
    void SetFailed(Exception exception);
    void SetStopping();
    void Reset();
}

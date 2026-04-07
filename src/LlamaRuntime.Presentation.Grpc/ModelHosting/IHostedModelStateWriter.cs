using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public interface IHostedModelStateWriter
{
    void SetLoading();
    void SetWarmingUp(IEngineModel model);
    void SetLoaded(IEngineModel model);
    void SetFailed(Exception exception);
    void SetStopping();
    void Reset();
}

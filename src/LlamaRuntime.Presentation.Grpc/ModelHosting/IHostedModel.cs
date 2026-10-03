using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public interface IHostedModel
{
    HostedModelSnapshot GetSnapshot();
    bool TryGetLoadedModel(out IEngineModel? model);
    void SetLoading();

    /// <summary>
    /// Transfers ownership on success. The caller retains ownership on rejection.
    /// Only the same model may be published again.
    /// </summary>
    void SetWarmingUp(IEngineModel model, string? configuredModelId = null);

    /// <inheritdoc cref="SetWarmingUp"/>
    void SetLoaded(IEngineModel model, string? configuredModelId = null);
    void SetFailed(Exception exception);
    void SetStopping();

    /// <summary>Does not release ownership or reopen a closed owner.</summary>
    void Reset();

    /// <summary>
    /// Permanently closes ownership and returns the shared cleanup task.
    /// DI disposal initiates cleanup without waiting.
    /// </summary>
    Task CloseAsync();
}

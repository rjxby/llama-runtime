using System.Threading;
using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Engine;

public sealed class HostedModelStore : IHostedModelStore
{
    private readonly Lock _gate = new();
    private HostedModelSnapshot _snapshot = new(HostedModelState.NotLoaded, null);

    public HostedModelSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public bool TryGetLoadedModel(out IEngineModel? model)
    {
        lock (_gate)
        {
            model = _snapshot.Model;
            return _snapshot.State == HostedModelState.Loaded && model != null;
        }
    }

    public void SetLoading()
    {
        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.Loading, null);
        }
    }

    public void SetLoaded(IEngineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.Loaded, model);
        }
    }

    public void SetFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.Failed, null, exception.Message);
        }
    }

    public void SetStopping()
    {
        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.Stopping, _snapshot.Model, _snapshot.FailureMessage);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.NotLoaded, null);
        }
    }
}

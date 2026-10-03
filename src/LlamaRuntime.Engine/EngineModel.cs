using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

public sealed class EngineModel : IEngineModel
{
    private readonly Lock _gate = new();
    private readonly ILlamaNative _native;
    private readonly LlamaModelHandle _modelHandle;
    private readonly ContextPool _pool;
    private Task? _cleanup;

    public string SourcePath { get; }
    public ModelMetadata Metadata { get; }

    public EngineModel(string sourcePath, LlamaModelHandle modelHandle, ModelMetadata metadata,
        ILlamaNative native, int poolSize, LlamaContextHandle? initialContext = null)
    {
        SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _modelHandle = modelHandle ?? throw new ArgumentNullException(nameof(modelHandle));
        if (poolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(poolSize));
        }

        _pool = new ContextPool(native, modelHandle, poolSize, initialContext);
    }

    public async Task<IInferenceSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var context = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return new LlamaSession(_native, context, _pool.Release);
    }

    public void Dispose() => _ = DisposeAsync();

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new ValueTask(_cleanup ??= CleanupAsync());
        }
    }

    private async Task CleanupAsync()
    {
        await _pool.CloseAsync().ConfigureAwait(false);
        // SafeHandle release does not depend on the DI-owned native wrapper still being usable.
        _modelHandle.Dispose();
    }
}

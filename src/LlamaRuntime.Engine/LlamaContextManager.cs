using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

public sealed class LlamaContextManager : ILlamaContextManager, IInferenceSessionFactory
{
    private readonly ILlamaNative _native;
    private readonly ILogger<LlamaContextManager> _logger;
    private readonly int _defaultPoolSize;
    private readonly ConcurrentDictionary<IEngineModel, ContextPool> _pools = new();
    private bool _disposed;

    public LlamaContextManager(ILlamaNative native, IOptions<LlamaProviderOptions> options, ILogger<LlamaContextManager> logger)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultPoolSize = options?.Value?.DefaultPoolSize ?? 1;
    }

    public async Task<IInferenceSession> CreateSessionAsync(IEngineModel model, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));

        var pool = GetPool(model);
        var ctx = await pool.AcquireAsync(ct).ConfigureAwait(false);

        return new LlamaSession(_native, ctx, handle => pool.Release(handle));
    }

    public async Task<TResult> WithContextAsync<TResult>(IEngineModel model, Func<LlamaContextHandle, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (action == null) throw new ArgumentNullException(nameof(action));

        var pool = GetPool(model);
        var ctx = await pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(ctx).ConfigureAwait(false);
        }
        finally
        {
            pool.Release(ctx);
        }
    }

    private ContextPool GetPool(IEngineModel model)
    {
        return _pools.GetOrAdd(model, m =>
        {
            var em = m as EngineModel ?? throw new InvalidOperationException("unexpected model type");
            return new ContextPool(_native, em.NativeModelHandle, _defaultPoolSize, _logger);
        });
    }

    public void ReleaseModelResources(IEngineModel model)
    {
        if (_pools.TryRemove(model, out var pool))
        {
            try { pool.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed disposing context pool"); }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LlamaContextManager));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kv in _pools)
        {
            try { kv.Value.Dispose(); } catch { }
        }

        _pools.Clear();
    }
}

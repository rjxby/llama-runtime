using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

public sealed class LlamaProvider : ILlamaProvider
{
    private readonly ILlamaNative _native;
    private readonly ILogger<LlamaProvider> _logger;
    private readonly int _defaultPoolSize;
    private readonly ConcurrentDictionary<IEngineModel, ContextPool> _pools = new();
    private bool _disposed;

    public LlamaProvider(ILlamaNative native, IOptions<LlamaProviderOptions> options, ILogger<LlamaProvider> logger)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultPoolSize = options?.Value?.DefaultPoolSize ?? 1;
    }

    public async Task<IEngineModel> LoadModelAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is null or empty", nameof(path));

        LlamaModelHandle modelHandle;
        try
        {
            modelHandle = await Task.Run(() => _native.LoadModel(path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model from {Path}", path);
            throw new ModelLoadException($"Failed to load model from {path}", ex);
        }

        var model = new EngineModel(path, modelHandle);
        var pool = new ContextPool(_native, modelHandle, _defaultPoolSize, _logger);
        _pools[model] = pool;
        _logger.LogInformation("Model loaded and pool created (path={Path})", path);
        return model;
    }

    public async Task UnloadModelAsync(IEngineModel model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) return;

        if (_pools.TryRemove(model, out var pool))
        {
            try { pool.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed disposing context pool for model {Model}", model.Id); }
        }

        try { await Task.Run(() => model.Dispose(), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed disposing model {Model}", model.Id); }
    }

    public async Task<string> InferAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (prompt == null) throw new ArgumentNullException(nameof(prompt));

        if (!_pools.TryGetValue(model, out var pool))
            throw new ModelNotFoundException($"Model {model.Id} is not loaded in provider");

        LlamaContextHandle ctx = null!;
        try
        {
            ctx = await pool.AcquireAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var em = model as EngineModel ?? throw new InvalidOperationException("unexpected model type");

            var result = await Task.Run(() =>
            {
                try
                {
                    return _native.Infer(em.NativeModelHandle, ctx, prompt);
                }
                catch (Exception ex)
                {
                    throw new InferenceException("Native inference failed", ex);
                }
            }).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InferenceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InferenceException("Inference failed", ex);
        }
        finally
        {
            if (ctx != null)
            {
                try { pool.Release(ctx); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed returning context to pool for model {Model}", model.Id);
                    try { ctx.Dispose(); } catch { }
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LlamaProvider));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kv in _pools)
        {
            try { kv.Value.Dispose(); } catch { }
            try { kv.Key.Dispose(); } catch { }
        }

        _pools.Clear();
    }
}

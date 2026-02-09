using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

internal sealed class ContextPool : IDisposable
{
    private readonly ILlamaNative _native;
    private readonly LlamaModelHandle _modelHandle;
    private readonly int _maxSize;
    private readonly ConcurrentQueue<LlamaContextHandle> _queue = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger _logger;
    private bool _disposed;

    public ContextPool(ILlamaNative native, LlamaModelHandle modelHandle, int maxSize, ILogger logger)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _modelHandle = modelHandle ?? throw new ArgumentNullException(nameof(modelHandle));
        _maxSize = Math.Max(1, maxSize);
        _semaphore = new SemaphoreSlim(_maxSize, _maxSize);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<LlamaContextHandle> AcquireAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        while (_queue.TryDequeue(out var ctx))
        {
            if (ctx == null) continue;
            if (ctx.IsInvalid)
            {
                try { ctx.Dispose(); } catch { }
                continue;
            }
            return ctx;
        }

        try
        {
            var newCtx = _native.CreateContext(_modelHandle);
            if (newCtx == null) throw new InvalidOperationException("native returned null context handle");
            return newCtx;
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    public void Release(LlamaContextHandle ctx)
    {
        if (ctx == null) return;
        if (_disposed)
        {
            try { ctx.Dispose(); } catch { }
            return;
        }

        if (ctx.IsInvalid)
        {
            try { ctx.Dispose(); } catch { }
            _semaphore.Release();
            return;
        }

        _queue.Enqueue(ctx);
        _semaphore.Release();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ContextPool));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_queue.TryDequeue(out var ctx))
        {
            try { ctx.Dispose(); } catch { }
        }

        _semaphore.Dispose();
    }
}
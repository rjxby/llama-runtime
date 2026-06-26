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
    private int _contextCount;
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
                DisposeTrackedContext(ctx);
                continue;
            }

            try
            {
                _native.ResetContext(ctx);
                return ctx;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reset pooled context. Disposing and creating new one.");
                DisposeTrackedContext(ctx);
                continue;
            }
        }

        var reservedContextSlot = false;
        try
        {
            ReserveContextSlot();
            reservedContextSlot = true;
            var newCtx = _native.CreateContext(_modelHandle);
            if (newCtx == null)
            {
                ReleaseContextSlot();
                reservedContextSlot = false;
                throw new InvalidOperationException("native returned null context handle");
            }
            return newCtx;
        }
        catch
        {
            if (reservedContextSlot)
            {
                ReleaseContextSlot();
            }

            _semaphore.Release();
            throw;
        }
    }

    public void Prime(LlamaContextHandle ctx)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.IsInvalid)
        {
            try { ctx.Dispose(); } catch { }
            return;
        }

        if (!TryReserveContextSlot())
        {
            _logger.LogWarning("Discarding primed context because the context pool is already at capacity.");
            try { ctx.Dispose(); } catch { }
            return;
        }

        _queue.Enqueue(ctx);
    }

    public void Release(LlamaContextHandle ctx)
    {
        if (ctx == null) return;
        if (_disposed)
        {
            try { ctx.Dispose(); } catch { }
            ReleaseContextSlot();
            return;
        }

        if (ctx.IsInvalid)
        {
            DisposeTrackedContext(ctx);
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

    private bool TryReserveContextSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _contextCount);
            if (current >= _maxSize)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _contextCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void ReserveContextSlot()
    {
        if (!TryReserveContextSlot())
        {
            throw new InvalidOperationException("Context pool is at capacity but no reusable context was available.");
        }
    }

    private void ReleaseContextSlot() => Interlocked.Decrement(ref _contextCount);

    private void DisposeTrackedContext(LlamaContextHandle ctx)
    {
        try { ctx.Dispose(); } catch { }
        ReleaseContextSlot();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_queue.TryDequeue(out var ctx))
        {
            DisposeTrackedContext(ctx);
        }

        _semaphore.Dispose();
    }
}

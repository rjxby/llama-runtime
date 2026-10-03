using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

internal sealed class ContextPool
{
    private readonly Lock _gate = new();
    private readonly ILlamaNative _native;
    private readonly LlamaModelHandle _modelHandle;
    private readonly Queue<LlamaContextHandle> _idle = new();
    private readonly SemaphoreSlim _available;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _acquiring;
    private int _leased;
    private bool _closing;
    private bool _idleDisposed;

    public ContextPool(ILlamaNative native, LlamaModelHandle modelHandle, int maxSize,
        LlamaContextHandle? initialContext)
    {
        _native = native;
        _modelHandle = modelHandle;
        _available = new SemaphoreSlim(maxSize, maxSize);
        if (initialContext != null)
        {
            _idle.Enqueue(initialContext);
        }
    }

    public async ValueTask<LlamaContextHandle> AcquireAsync(CancellationToken cancellationToken)
    {
        CancellationToken closedToken;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _acquiring++;
            closedToken = _closed.Token;
        }

        var reserved = false;
        LlamaContextHandle? context = null;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closedToken);
            await _available.WaitAsync(wait.Token).ConfigureAwait(false);
            reserved = true;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                if (_idle.TryDequeue(out var existing))
                {
                    context = existing;
                }
            }

            context ??= _native.CreateContext(_modelHandle);
            if (context == null || context.IsInvalid || context.IsClosed)
            {
                throw new InvalidOperationException("Native runtime returned an invalid context handle.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                _leased++;
            }
            var result = context;
            context = null;
            reserved = false;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && closedToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ContextPool));
        }
        finally
        {
            context?.Dispose();
            lock (_gate)
            {
                if (reserved)
                {
                    _available.Release();
                }

                _acquiring--;
                CompleteClose();
            }
        }
    }

    public void Release(LlamaContextHandle context)
    {
        lock (_gate)
        {
            if (!_closing && !context.IsInvalid && !context.IsClosed)
            {
                _idle.Enqueue(context);
                _leased--;
                _available.Release();
                return;
            }
        }
        // Keep the lease counted until native context destruction has finished.
        context.Dispose();
        lock (_gate)
        {
            _leased--;
            _available.Release();
            CompleteClose();
        }
    }

    public Task CloseAsync()
    {
        LlamaContextHandle[] idle;
        lock (_gate)
        {
            if (_closing)
            {
                return _drained.Task;
            }

            _closing = true;
            idle = _idle.ToArray();
            _idle.Clear();
        }
        _closed.Cancel();
        foreach (var context in idle)
        {
            context.Dispose();
        }

        lock (_gate)
        {
            _idleDisposed = true;
            CompleteClose();
        }
        return _drained.Task;
    }

    private void CompleteClose()
    {
        if (!_closing || !_idleDisposed || _acquiring != 0 || _leased != 0)
        {
            return;
        }

        _available.Dispose();
        _closed.Dispose();
        _drained.TrySetResult();
    }
}

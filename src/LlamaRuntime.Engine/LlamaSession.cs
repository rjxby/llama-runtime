using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

internal sealed class LlamaSession : IInferenceSession
{
    private readonly ILlamaNative _native;
    private readonly LlamaContextHandle _handle;
    private readonly Action<LlamaContextHandle> _onDispose;
    private bool _disposed;

    public LlamaSession(ILlamaNative native, LlamaContextHandle handle, Action<LlamaContextHandle> onDispose)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _onDispose = onDispose ?? throw new ArgumentNullException(nameof(onDispose));
    }

    public async Task<string> InferAsync(string prompt, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(LlamaSession));

        return await Task.Run(() => _native.Infer(_handle, prompt), ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _onDispose(_handle);
        return ValueTask.CompletedTask;
    }
}

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

internal sealed class EngineModel : IEngineModel
{
    public string Id { get; }
    internal LlamaModelHandle NativeModelHandle { get; }

    private bool _disposed;

    public EngineModel(string id, LlamaModelHandle nativeModelHandle)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        NativeModelHandle = nativeModelHandle ?? throw new ArgumentNullException(nameof(nativeModelHandle));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { NativeModelHandle.Dispose(); } catch { }
    }
}

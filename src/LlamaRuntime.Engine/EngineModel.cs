using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

public sealed class EngineModel : IEngineModel
{
    public string SourcePath { get; }
    public ModelMetadata? Metadata { get; }
    internal LlamaModelHandle NativeModelHandle { get; }

    private bool _disposed;

    public EngineModel(string sourcePath, LlamaModelHandle nativeModelHandle, ModelMetadata? metadata = null)
    {
        SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
        NativeModelHandle = nativeModelHandle ?? throw new ArgumentNullException(nameof(nativeModelHandle));
        Metadata = metadata;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { NativeModelHandle.Dispose(); } catch { }
    }
}

using System.Runtime.InteropServices;

namespace LlamaRuntime.Native.Contracts;

/// <summary>
/// Low-level native adapter abstraction. Implementations must be thread-safe.
/// </summary>
public interface ILlamaNative : IDisposable
{
    string GetVersion();

    LlamaModelHandle LoadModel(string path);
    void UnloadModel(LlamaModelHandle model);

    LlamaContextHandle CreateContext(LlamaModelHandle model);
    void RemoveContext(LlamaContextHandle ctx);

    string Infer(LlamaModelHandle model, LlamaContextHandle ctx, string prompt);
}

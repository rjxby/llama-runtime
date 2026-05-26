using System.Runtime.InteropServices;

namespace LlamaRuntime.Native.Contracts;

/// <summary>
/// Low-level native adapter abstraction. Implementations must be thread-safe.
/// </summary>
public interface ILlamaNative : IDisposable
{
    string GetVersion();

    LlamaModelHandle LoadModel(string path);
    NativeModelMetadata GetModelMetadata(LlamaModelHandle model);
    NativeContextMetadata GetContextMetadata(LlamaContextHandle context);
    void UnloadModel(LlamaModelHandle model);

    LlamaContextHandle CreateContext(LlamaModelHandle model);
    void RemoveContext(LlamaContextHandle ctx);
    void ResetContext(LlamaContextHandle ctx);
    int CountTokens(LlamaContextHandle ctx, string prompt);

    NativeInferenceResult Infer(
        LlamaContextHandle ctx,
        string prompt,
        NativeInferenceResponseFormat responseFormat = NativeInferenceResponseFormat.Text,
        string? grammar = null);
}

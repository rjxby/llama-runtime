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

    public Task<InferenceResult> InferAsync(
        string prompt,
        CancellationToken ct = default,
        InferenceResponseFormat responseFormat = InferenceResponseFormat.Text,
        string? grammar = null,
        InferenceGenerationOptions? generationOptions = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(LlamaSession));
        ct.ThrowIfCancellationRequested();

        var nativeResponseFormat = MapResponseFormat(responseFormat);
        var nativeGenerationOptions = generationOptions is null
            ? null
            : new NativeGenerationOptions(
                generationOptions.MaxOutputTokens,
                generationOptions.Temperature,
                generationOptions.TopP);
        var result = _native.Infer(_handle, prompt, nativeResponseFormat, grammar, nativeGenerationOptions, ct);
        return Task.FromResult(new InferenceResult(
            result.Content,
            result.InputTokens,
            result.OutputTokens,
            result.TotalTokens));
    }

    public Task<int> CountTokensAsync(string prompt, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(LlamaSession));
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(_native.CountTokens(_handle, prompt));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _onDispose(_handle);
        return ValueTask.CompletedTask;
    }

    private static NativeInferenceResponseFormat MapResponseFormat(InferenceResponseFormat responseFormat) =>
        responseFormat switch
        {
            InferenceResponseFormat.Text => NativeInferenceResponseFormat.Text,
            InferenceResponseFormat.Json => NativeInferenceResponseFormat.Grammar,
            _ => throw new ArgumentOutOfRangeException(nameof(responseFormat), responseFormat, "Unknown response format.")
        };
}

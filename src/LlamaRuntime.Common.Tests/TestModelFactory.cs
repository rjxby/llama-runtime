using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

using LlamaRuntime.Engine;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;

namespace LlamaRuntime.Common.Tests;

public static class TestModelFactory
{
    public static LlamaModelHandle CreateModelHandle(nint value = 1) =>
        LlamaModelHandle.FromIntPtr(new IntPtr(value));

    public static LlamaContextHandle CreateContextHandle(nint value = 1) =>
        LlamaContextHandle.FromIntPtr(new IntPtr(value));

    public static ModelMetadata CreateModelMetadata(
        int contextSize = 32,
        NativeTokenizerType tokenizerType = NativeTokenizerType.SentencePiece,
        int? trainingContextSize = null) =>
        new(contextSize, tokenizerType, trainingContextSize ?? contextSize);

    public static IEngineModel CreateEngineModel(
        string sourcePath = "model.gguf",
        int contextSize = 32,
        NativeTokenizerType tokenizerType = NativeTokenizerType.SentencePiece,
        int? trainingContextSize = null,
        nint handleValue = 1) =>
        new MetadataModel(sourcePath, CreateModelMetadata(contextSize, tokenizerType, trainingContextSize));

    private sealed record MetadataModel(string SourcePath, ModelMetadata Metadata) : IEngineModel
    {
        public Task<IInferenceSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Metadata-only test model cannot run inference.");
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public static IOptions<LlamaNativeOptions> CreateNativeOptions(
        int contextSize = 32,
        int generationMaxNewTokens = 8,
        int batchSize = 8,
        string nativeLibraryPath = "test-native") =>
        Options.Create(new LlamaNativeOptions
        {
            NativeLibraryPath = nativeLibraryPath,
            ContextSize = contextSize,
            BatchSize = batchSize,
            GenerationMaxNewTokens = generationMaxNewTokens
        });
}

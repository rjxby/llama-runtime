using Microsoft.Extensions.Options;

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

    public static EngineModel CreateEngineModel(
        string sourcePath = "model.gguf",
        int contextSize = 32,
        NativeTokenizerType tokenizerType = NativeTokenizerType.SentencePiece,
        int? trainingContextSize = null,
        nint handleValue = 1) =>
        new(
            sourcePath,
            CreateModelHandle(handleValue),
            CreateModelMetadata(contextSize, tokenizerType, trainingContextSize));

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

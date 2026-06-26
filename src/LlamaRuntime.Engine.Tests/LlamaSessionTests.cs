using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LlamaRuntime.Engine.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class LlamaSessionTests
{
    [Fact]
    public async Task ProviderInferAsync_WithGenerationOptions_MapsOptionsToNative()
    {
        var native = new Mock<ILlamaNative>();
        var modelHandle = TestModelFactory.CreateModelHandle();
        var contextHandle = TestModelFactory.CreateContextHandle(7);
        var generationOptions = new InferenceGenerationOptions(4, 0.7f, 0.8f);
        using var cts = new CancellationTokenSource();

        native.Setup(n => n.LoadModel("model.gguf"))
            .Returns(modelHandle);
        native.Setup(n => n.GetModelMetadata(modelHandle))
            .Returns(new NativeModelMetadata(4096, NativeTokenizerType.SentencePiece));
        native.Setup(n => n.CreateContext(modelHandle))
            .Returns(contextHandle);
        native.Setup(n => n.GetContextMetadata(contextHandle))
            .Returns(new NativeContextMetadata(4096));
        native.Setup(n => n.CountTokens(contextHandle, "prompt"))
            .Returns(2);
        native.Setup(n => n.Infer(
                contextHandle,
                "prompt",
                NativeInferenceResponseFormat.Text,
                null,
                It.Is<NativeGenerationOptions>(options =>
                    options.MaxNewTokens == 4 &&
                    Math.Abs(options.Temperature - 0.7f) < 0.0001f &&
                    Math.Abs(options.TopP - 0.8f) < 0.0001f),
                cts.Token))
            .Returns(new NativeInferenceResult("ok", 2, 4, 6));

        using var contextManager = new LlamaContextManager(
            native.Object,
            Options.Create(new InferenceOptions { WorkerCount = 1 }),
            NullLogger<LlamaContextManager>.Instance);
        using var provider = new LlamaProvider(
            native.Object,
            contextManager,
            Options.Create(new LlamaNativeOptions
            {
                NativeLibraryPath = "test-native",
                ContextSize = 4096,
                GenerationMaxNewTokens = 512
            }),
            NullLogger<LlamaProvider>.Instance);

        var model = await provider.LoadModelAsync("model.gguf");
        var result = await provider.InferAsync(
            model,
            "prompt",
            cts.Token,
            responseFormat: InferenceResponseFormat.Text,
            generationOptions: generationOptions);

        Assert.Equal("ok", result.Content);
        native.VerifyAll();
    }
}

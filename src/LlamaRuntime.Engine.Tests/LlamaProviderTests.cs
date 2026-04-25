using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Common.Tests;

namespace LlamaRuntime.Engine.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class LlamaProviderTests
{
    private static LlamaModelHandle CreateModelHandle() =>
        TestModelFactory.CreateModelHandle();

    private static LlamaProvider CreateProvider(
        Mock<ILlamaNative> nativeMock,
        Mock<ILlamaContextManager> contextManagerMock,
        int contextSize = 4096,
        int generationMaxNewTokens = 512)
    {
        return new LlamaProvider(
            nativeMock.Object,
            contextManagerMock.Object,
            Options.Create(new LlamaNativeOptions
            {
                NativeLibraryPath = "test-native",
                ContextSize = contextSize,
                GenerationMaxNewTokens = generationMaxNewTokens
            }),
            NullLogger<LlamaProvider>.Instance);
    }

    private static NativeModelMetadata CreateMetadata(
        int trainingContextSize = 4096,
        NativeTokenizerType tokenizerType = NativeTokenizerType.SentencePiece) =>
        new(trainingContextSize, tokenizerType);

    private static NativeContextMetadata CreateContextMetadata(int contextSize = 4096) =>
        new(contextSize);

    [Fact]
    public async Task LoadModelAsync_Returns_Model()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        using var provider = CreateProvider(native, contextManager);

        var model = await provider.LoadModelAsync("model.gguf");

        Assert.NotNull(model);
        Assert.Equal("model.gguf", model.SourcePath);
        Assert.Equal(4096, model.Metadata?.ContextSize);
        Assert.Equal(4096, model.Metadata?.TrainingContextSize);
        Assert.Equal(NativeTokenizerType.SentencePiece, model.Metadata?.TokenizerType);
        native.Verify(n => n.LoadModel("model.gguf"), Times.Once);
        contextManager.Verify(m => m.PrimeModelContext(model, It.IsAny<LlamaContextHandle>()), Times.Once);
    }

    [Fact]
    public async Task LoadModelAsync_NativeFailure_Throws_ModelLoadException()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Throws(new NativeLoadModelException("fail"));

        using var provider = CreateProvider(native, contextManager);

        await Assert.ThrowsAsync<ModelLoadException>(() =>
            provider.LoadModelAsync("model.gguf"));
    }

    [Theory]
    [InlineData(NativeTokenizerType.SentencePiece)]
    [InlineData(NativeTokenizerType.Bpe)]
    [InlineData(NativeTokenizerType.WordPiece)]
    [InlineData(NativeTokenizerType.Unigram)]
    [InlineData(NativeTokenizerType.Rwkv)]
    [InlineData(NativeTokenizerType.Plamo2)]
    [InlineData(NativeTokenizerType.None)]
    [InlineData(NativeTokenizerType.Unknown)]
    public async Task LoadModelAsync_PreservesTypedTokenizerMetadata(
        NativeTokenizerType tokenizerType)
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata(tokenizerType: tokenizerType));
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        using var provider = CreateProvider(native, contextManager);

        var model = await provider.LoadModelAsync("model.gguf");

        Assert.Equal(tokenizerType, model.Metadata?.TokenizerType);
    }

    [Fact]
    public async Task LoadModelAsync_ContextMismatch_ThrowsModelLoadException()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata(trainingContextSize: 16));
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata(contextSize: 16));

        using var provider = CreateProvider(native, contextManager, contextSize: 32);

        var ex = await Assert.ThrowsAsync<ModelLoadException>(() =>
            provider.LoadModelAsync("model.gguf"));

        Assert.Contains("Configured context size 32 does not match actual created context size 16", ex.Message);
    }

    [Fact]
    public async Task LoadModelAsync_LowerTrainingContextButMatchingActualContext_Succeeds()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata(trainingContextSize: 16));
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata(contextSize: 32));

        using var provider = CreateProvider(native, contextManager, contextSize: 32);

        var model = await provider.LoadModelAsync("model.gguf");

        Assert.Equal(32, model.Metadata?.ContextSize);
        Assert.Equal(16, model.Metadata?.TrainingContextSize);
    }

    [Fact]
    public async Task InferAsync_Uses_Sessions()
    {
        var modelHandle = CreateModelHandle();

        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(modelHandle);
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        contextManager
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("ok", 2, 1, 3));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var result = await provider.InferAsync(model, "hi");

        Assert.Equal("ok", result.Content);
        Assert.Equal(2, result.InputTokens);
        Assert.Equal(1, result.OutputTokens);
        Assert.Equal(3, result.TotalTokens);
        contextManager.Verify(m => m.CreateSessionAsync(model, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.InferAsync("hi", It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InferAsync_BlankOutput_ThrowsEmptyInferenceOutputException(string output)
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        contextManager
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult(output, 2, 1, 3));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<EmptyInferenceOutputException>(() =>
            provider.InferAsync(model, "hi"));

        Assert.Equal("Inference returned blank output for a text-generation request.", ex.Message);
    }

    [Fact]
    public async Task UnloadModelAsync_Calls_ReleaseModelResources()
    {
        var modelHandle = CreateModelHandle();
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(modelHandle);
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        await provider.UnloadModelAsync(model);

        contextManager.Verify(m => m.ReleaseModelResources(model), Times.Once);
    }

    [Fact]
    public async Task CountTokensAsync_UsesSessionTokenizer()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        contextManager
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        session
            .Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        session
            .Setup(s => s.CountTokensAsync("hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var tokenCount = await provider.CountTokensAsync(model, "hello");

        Assert.Equal(5, tokenCount);
    }

    [Fact]
    public async Task InferAsync_OversizedPrompt_ThrowsPromptBudgetExceededException()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata(trainingContextSize: 8));
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata(contextSize: 8));

        contextManager
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NativeInvalidArgumentException("too long"));

        using var provider = CreateProvider(native, contextManager, contextSize: 8, generationMaxNewTokens: 2);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<PromptBudgetExceededException>(() =>
            provider.InferAsync(model, "short prompt"));

        Assert.Contains("Prompt exceeds input budget", ex.Message);
    }

    [Fact]
    public async Task InferAsync_NativeFailure_ThrowsInferenceException()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        contextManager
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NativeIOException("native io fail"));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<InferenceException>(() =>
            provider.InferAsync(model, "short prompt"));

        Assert.Equal("Inference failed in the native runtime.", ex.Message);
    }

    [Fact]
    public async Task InferAsync_NativeFailureAfterSessionCreation_ThrowsInferenceException()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        contextManager
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NativeIOException("native io fail"));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<InferenceException>(() =>
            provider.InferAsync(model, "prompt"));

        Assert.Equal("Inference failed in the native runtime.", ex.Message);
    }

    [Fact]
    public async Task InferAsync_CancellationDuringInference_Propagates()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();
        using var cts = new CancellationTokenSource();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        contextManager
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            });

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.InferAsync(model, "prompt", cts.Token));
    }
}

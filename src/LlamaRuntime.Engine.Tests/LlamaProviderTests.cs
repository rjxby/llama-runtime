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
        LlamaModelHandle.FromIntPtr(new IntPtr(1));

    private static LlamaContextHandle CreateContextHandle(int id) =>
        LlamaContextHandle.FromIntPtr(new IntPtr(id));

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

    [Fact]
    public async Task LoadModelAsync_Returns_Model()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());

        using var provider = CreateProvider(native, contextManager);

        var model = await provider.LoadModelAsync("model.gguf");

        Assert.NotNull(model);
        Assert.Equal("model.gguf", model.Id);
        native.Verify(n => n.LoadModel("model.gguf"), Times.Once);
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

    [Fact]
    public async Task InferAsync_Uses_Sessions()
    {
        var modelHandle = CreateModelHandle();
        var ctxHandle = CreateContextHandle(1);

        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var sessionFactory = contextManager.As<IInferenceSessionFactory>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(modelHandle);

        sessionFactory
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        session
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var result = await provider.InferAsync(model, "hi");

        Assert.Equal("ok", result);
        sessionFactory.Verify(m => m.CreateSessionAsync(model, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.CountTokensAsync("hi", It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.InferAsync("hi", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnloadModelAsync_Calls_ReleaseModelResources()
    {
        var modelHandle = CreateModelHandle();
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(modelHandle);

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
        var sessionFactory = contextManager.As<IInferenceSessionFactory>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());

        sessionFactory
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

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
        var sessionFactory = contextManager.As<IInferenceSessionFactory>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());

        sessionFactory
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);

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
        var sessionFactory = contextManager.As<IInferenceSessionFactory>();
        var session = new Mock<IInferenceSession>();

        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());

        sessionFactory
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        session
            .Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(12);

        session
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NativeIOException("native io fail"));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<InferenceException>(() =>
            provider.InferAsync(model, "short prompt"));

        Assert.Equal("Inference failed in the native runtime.", ex.Message);
    }
}

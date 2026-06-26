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

    [Fact]
    public async Task LoadModelAsync_WhenCancelledAfterNativeLoad_UnloadsModelAndPreservesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var modelHandle = CreateModelHandle();
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel("model.gguf"))
            .Returns(() =>
            {
                cts.Cancel();
                return modelHandle;
            });

        using var provider = CreateProvider(native, contextManager);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.LoadModelAsync("model.gguf", cts.Token));

        native.Verify(n => n.UnloadModel(modelHandle), Times.Once);
    }

    [Fact]
    public async Task LoadModelAsync_WhenCancelledAfterProbeContext_RemovesContextAndUnloadsModel()
    {
        using var cts = new CancellationTokenSource();
        var modelHandle = CreateModelHandle();
        var contextHandle = TestModelFactory.CreateContextHandle(2);
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel("model.gguf"))
            .Returns(modelHandle);
        native.Setup(n => n.GetModelMetadata(modelHandle))
            .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(modelHandle))
            .Returns(contextHandle);
        native.Setup(n => n.GetContextMetadata(contextHandle))
            .Returns(() =>
            {
                cts.Cancel();
                return CreateContextMetadata();
            });

        using var provider = CreateProvider(native, contextManager);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.LoadModelAsync("model.gguf", cts.Token));

        native.Verify(n => n.RemoveContext(contextHandle), Times.Once);
        native.Verify(n => n.UnloadModel(modelHandle), Times.Once);
    }

    [Fact]
    public async Task LoadModelAsync_WhenProbeMetadataFails_RemovesContextAndUnloadsModel()
    {
        var modelHandle = CreateModelHandle();
        var contextHandle = TestModelFactory.CreateContextHandle(2);
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        native.Setup(n => n.LoadModel("model.gguf"))
            .Returns(modelHandle);
        native.Setup(n => n.GetModelMetadata(modelHandle))
            .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(modelHandle))
            .Returns(contextHandle);
        native.Setup(n => n.GetContextMetadata(contextHandle))
            .Throws(new NativeInvalidArgumentException("metadata failed"));

        using var provider = CreateProvider(native, contextManager);

        var ex = await Assert.ThrowsAsync<ModelLoadException>(() =>
            provider.LoadModelAsync("model.gguf"));

        Assert.Contains("Failed to determine actual runtime context size", ex.Message);
        native.Verify(n => n.RemoveContext(contextHandle), Times.Once);
        native.Verify(n => n.UnloadModel(modelHandle), Times.Once);
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
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ReturnsAsync(new InferenceResult("ok", 2, 1, 3));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var result = await provider.InferAsync(model, "hi");

        Assert.Equal("ok", result.Content);
        Assert.Equal(2, result.InputTokens);
        Assert.Equal(1, result.OutputTokens);
        Assert.Equal(3, result.TotalTokens);
        contextManager.Verify(m => m.CreateSessionAsync(model, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text), Times.Once);
        session.Verify(s => s.CountTokensAsync("hi", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InferAsync_AcceptsCancellationTokenAsThirdPositionalArgument()
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
            .Setup(m => m.CreateSessionAsync(It.IsAny<IEngineModel>(), cts.Token))
            .ReturnsAsync(session.Object);
        session
            .Setup(s => s.InferAsync("hi", cts.Token, InferenceResponseFormat.Text))
            .ReturnsAsync(new InferenceResult("ok", 2, 1, 3));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var result = await provider.InferAsync(model, "hi", cts.Token);

        Assert.Equal("ok", result.Content);
        session.Verify(s => s.InferAsync("hi", cts.Token, InferenceResponseFormat.Text), Times.Once);
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
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ReturnsAsync(new InferenceResult(output, 2, 1, 3));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<EmptyInferenceOutputException>(() =>
            provider.InferAsync(model, "hi"));

        Assert.Equal("Inference returned blank output for a text-generation request.", ex.Message);
    }

    [Fact]
    public async Task InferAsync_NativeEmptyOutput_ThrowsEmptyInferenceOutputException()
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
            .Setup(s => s.CountTokensAsync("hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        session
            .Setup(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text, null, null))
            .ThrowsAsync(new NativeEmptyOutputException("empty"));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<EmptyInferenceOutputException>(() =>
            provider.InferAsync(model, "hi"));

        Assert.Equal("Inference returned blank output for a text-generation request.", ex.Message);
    }

    [Fact]
    public async Task InferAsync_JsonModeWithoutSchema_AcceptsValidObjectJson()
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
            .Setup(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json, JsonStructuredOutput.AnyObjectGrammar))
            .ReturnsAsync(new InferenceResult("""{"ok":true}""", 2, 4, 6));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var result = await provider.InferAsync(model, "hi", responseFormat: InferenceResponseFormat.Json);

        Assert.Equal("""{"ok":true}""", result.Content);
        session.Verify(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json, JsonStructuredOutput.AnyObjectGrammar), Times.Once);
    }

    [Theory]
    [InlineData("not json", "Inference did not return a valid JSON object.")]
    [InlineData("[1,2,3]", "Inference did not return a JSON object at the root.")]
    [InlineData("\"value\"", "Inference did not return a JSON object at the root.")]
    public async Task InferAsync_JsonModeWithoutSchema_RejectsInvalidOrNonObjectJson(
        string output,
        string expectedMessage)
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
            .Setup(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json, JsonStructuredOutput.AnyObjectGrammar))
            .ReturnsAsync(new InferenceResult(output, 2, 4, 6));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<StructuredOutputException>(() =>
            provider.InferAsync(model, "hi", responseFormat: InferenceResponseFormat.Json));

        Assert.Equal(expectedMessage, ex.Message);
    }

    [Fact]
    public async Task InferAsync_JsonModeWithSchema_AcceptsValidSchemaOutput()
    {
        const string schema = """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"],"additionalProperties":false}""";
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
            .Setup(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json, It.Is<string>(grammar => grammar.Contains("schema-0", StringComparison.Ordinal))))
            .ReturnsAsync(new InferenceResult("""{"title":"ok"}""", 2, 4, 6));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var result = await provider.InferAsync(model, "hi", responseFormat: InferenceResponseFormat.Json, jsonSchema: schema);

        Assert.Equal("""{"title":"ok"}""", result.Content);
        session.Verify(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json, It.Is<string>(grammar => grammar.Contains("schema-0", StringComparison.Ordinal))), Times.Once);
    }

    [Theory]
    [InlineData("{}", "$.title is required.")]
    [InlineData("""{"title":"ok","extra":"no"}""", "$.extra is not allowed by the JSON schema.")]
    public async Task InferAsync_JsonModeWithSchema_RejectsOutputThatDoesNotMatchSchema(
        string output,
        string expectedMessage)
    {
        const string schema = """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"],"additionalProperties":false}""";
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
            .Setup(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json, It.IsAny<string>()))
            .ReturnsAsync(new InferenceResult(output, 2, 4, 6));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<StructuredOutputException>(() =>
            provider.InferAsync(model, "hi", responseFormat: InferenceResponseFormat.Json, jsonSchema: schema));

        Assert.Equal(expectedMessage, ex.Message);
    }

    [Fact]
    public async Task InferAsync_TextMode_DoesNotValidateJson()
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
            .Setup(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ReturnsAsync(new InferenceResult("not json", 2, 2, 4));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var result = await provider.InferAsync(model, "hi");

        Assert.Equal("not json", result.Content);
    }

    [Fact]
    public async Task InferAsync_WithGenerationOptions_ForwardsOptionsToSession()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();
        var generationOptions = new InferenceGenerationOptions(4, 0.7f, 0.8f);

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
            .Setup(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text, null, generationOptions))
            .ReturnsAsync(new InferenceResult("ok", 2, 4, 6));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var result = await provider.InferAsync(model, "hi", generationOptions: generationOptions);

        Assert.Equal("ok", result.Content);
        session.Verify(
            s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text, null, generationOptions),
            Times.Once);
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
            .Setup(s => s.CountTokensAsync("short prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        using var provider = CreateProvider(native, contextManager, contextSize: 8, generationMaxNewTokens: 2);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<PromptBudgetExceededException>(() =>
            provider.InferAsync(model, "short prompt"));

        Assert.Contains("Prompt exceeds input budget", ex.Message);
    }

    [Fact]
    public async Task InferAsync_OversizedPrompt_UsesRequestMaxOutputTokensInPromptBudget()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var session = new Mock<IInferenceSession>();
        var generationOptions = new InferenceGenerationOptions(4, 0.0f, 1.0f);

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
            .Setup(s => s.CountTokensAsync("short prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        using var provider = CreateProvider(native, contextManager, contextSize: 8, generationMaxNewTokens: 6);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<PromptBudgetExceededException>(() =>
            provider.InferAsync(model, "short prompt", generationOptions: generationOptions));

        Assert.Contains("reserved output 4", ex.Message);
        Assert.Contains("only 4 are allowed", ex.Message);
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
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ThrowsAsync(new NativeIOException("native io fail"));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<InferenceException>(() =>
            provider.InferAsync(model, "short prompt"));

        Assert.Equal("Inference failed in the native runtime.", ex.Message);
    }

    [Fact]
    public async Task InferAsync_NativeInvalidArgument_WhenPromptFits_ThrowsInferenceException()
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
            .Setup(s => s.CountTokensAsync("short prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        session
            .Setup(s => s.InferAsync("short prompt", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text, null, null))
            .ThrowsAsync(new NativeInvalidArgumentException("bad native arg"));

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        var ex = await Assert.ThrowsAsync<InferenceException>(() =>
            provider.InferAsync(model, "short prompt"));

        Assert.Equal("Inference failed.", ex.Message);
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
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
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
            .Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .Returns(async () =>
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            });

        using var provider = CreateProvider(native, contextManager);
        var model = await provider.LoadModelAsync("model");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.InferAsync(model, "prompt", cancellationToken: cts.Token));
    }
}

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
        int contextSize = 4096,
        int generationMaxNewTokens = 512)
    {
        return new LlamaProvider(
            nativeMock.Object,
            Options.Create(new LlamaRuntime.Engine.Contracts.Configuration.InferenceOptions { WorkerCount = 2 }),
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
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        using var provider = CreateProvider(native);

        var model = await provider.LoadModelAsync("model.gguf");

        Assert.NotNull(model);
        Assert.Equal("model.gguf", model.SourcePath);
        Assert.Equal(4096, model.Metadata?.ContextSize);
        Assert.Equal(4096, model.Metadata?.TrainingContextSize);
        Assert.Equal(NativeTokenizerType.SentencePiece, model.Metadata?.TokenizerType);
        native.Verify(n => n.LoadModel("model.gguf"), Times.Once);
        await using var session = await model.CreateSessionAsync();
        native.Verify(n => n.CreateContext(It.IsAny<LlamaModelHandle>()), Times.Once);
    }

    [Fact]
    public async Task LoadModelAsync_NativeFailure_Throws_ModelLoadException()
    {
        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Throws(new NativeLoadModelException("fail"));

        using var provider = CreateProvider(native);

        await Assert.ThrowsAsync<ModelLoadException>(() =>
            provider.LoadModelAsync("model.gguf"));
    }

    [Fact]
    public async Task LoadModelAsync_WhenCancelledAfterNativeLoad_UnloadsModelAndPreservesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var modelHandle = CreateModelHandle();
        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.LoadModel("model.gguf"))
            .Returns(() =>
            {
                cts.Cancel();
                return modelHandle;
            });

        using var provider = CreateProvider(native);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.LoadModelAsync("model.gguf", cts.Token));

        Assert.True(modelHandle.IsClosed);
    }

    [Fact]
    public async Task LoadModelAsync_WhenCancelledAfterProbeContext_RemovesContextAndUnloadsModel()
    {
        using var cts = new CancellationTokenSource();
        var modelHandle = CreateModelHandle();
        var contextHandle = TestModelFactory.CreateContextHandle(2);
        var native = new Mock<ILlamaNative>();
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

        using var provider = CreateProvider(native);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.LoadModelAsync("model.gguf", cts.Token));

        Assert.True(contextHandle.IsClosed);
        Assert.True(modelHandle.IsClosed);
    }

    [Fact]
    public async Task LoadModelAsync_WhenProbeMetadataFails_RemovesContextAndUnloadsModel()
    {
        var modelHandle = CreateModelHandle();
        var contextHandle = TestModelFactory.CreateContextHandle(2);
        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.LoadModel("model.gguf"))
            .Returns(modelHandle);
        native.Setup(n => n.GetModelMetadata(modelHandle))
            .Returns(CreateMetadata());
        native.Setup(n => n.CreateContext(modelHandle))
            .Returns(contextHandle);
        native.Setup(n => n.GetContextMetadata(contextHandle))
            .Throws(new NativeInvalidArgumentException("metadata failed"));

        using var provider = CreateProvider(native);

        var ex = await Assert.ThrowsAsync<ModelLoadException>(() =>
            provider.LoadModelAsync("model.gguf"));

        Assert.Contains("Failed to determine actual runtime context size", ex.Message);
        Assert.True(contextHandle.IsClosed);
        Assert.True(modelHandle.IsClosed);
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
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata(tokenizerType: tokenizerType));
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata());

        using var provider = CreateProvider(native);

        var model = await provider.LoadModelAsync("model.gguf");

        Assert.Equal(tokenizerType, model.Metadata?.TokenizerType);
    }

    [Fact]
    public async Task LoadModelAsync_ContextMismatch_ThrowsModelLoadException()
    {
        var modelHandle = CreateModelHandle();
        var contextHandle = TestModelFactory.CreateContextHandle(2);
        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(modelHandle);
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata(trainingContextSize: 16));
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(contextHandle);
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata(contextSize: 16));

        using var provider = CreateProvider(native, contextSize: 32);

        var ex = await Assert.ThrowsAsync<ModelLoadException>(() =>
            provider.LoadModelAsync("model.gguf"));

        Assert.Contains("Configured context size 32 does not match actual created context size 16", ex.Message);
        Assert.True(contextHandle.IsClosed);
        Assert.True(modelHandle.IsClosed);
    }

    [Fact]
    public async Task LoadModelAsync_LowerTrainingContextButMatchingActualContext_Succeeds()
    {
        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.LoadModel(It.IsAny<string>()))
              .Returns(CreateModelHandle());
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
              .Returns(CreateMetadata(trainingContextSize: 16));
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
              .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
              .Returns(CreateContextMetadata(contextSize: 32));

        using var provider = CreateProvider(native, contextSize: 32);

        var model = await provider.LoadModelAsync("model.gguf");

        Assert.Equal(32, model.Metadata?.ContextSize);
        Assert.Equal(16, model.Metadata?.TrainingContextSize);
    }

    private static (Mock<IEngineModel> Model, Mock<IInferenceSession> Session) SessionModel()
    {
        var model = new Mock<IEngineModel>();
        var session = new Mock<IInferenceSession>();
        model.SetupGet(m => m.Metadata).Returns(TestModelFactory.CreateModelMetadata(4096));
        model.Setup(m => m.CreateSessionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        return (model, session);
    }

    [Fact]
    public async Task PreparedRequest_ForwardsOptionsConstraintAndUsage_AndReturnsLease()
    {
        var (model, session) = SessionModel();
        var generation = new InferenceGenerationOptions(4, 0.7f, 0.8f);
        var constraint = JsonStructuredOutput.Parse(null);
        var request = new PreparedGenerationRequest("hi", generation, constraint);
        session.Setup(s => s.InferAsync("hi", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json,
                constraint.Grammar, generation))
            .ReturnsAsync(new InferenceResult("{\"ok\":true}", 2, 4, 6));
        using var provider = CreateProvider(new Mock<ILlamaNative>());
        var result = await provider.InferAsync(model.Object, request);
        Assert.Equal(2, result.InputTokens);
        Assert.Equal(4, result.OutputTokens);
        Assert.Equal(6, result.TotalTokens);
        session.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData("", null, "Inference returned blank output for a text-generation request.")]
    [InlineData("   ", null, "Inference returned blank output for a text-generation request.")]
    [InlineData("not json", "{}", "Inference did not return a valid JSON object.")]
    [InlineData("[1,2,3]", "{}", "Inference did not return a JSON object at the root.")]
    [InlineData("\"value\"", "{}", "Inference did not return a JSON object at the root.")]
    [InlineData("{}", "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"],\"additionalProperties\":false}", "$.title is required.")]
    [InlineData("{\"title\":\"ok\",\"extra\":\"no\"}", "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"],\"additionalProperties\":false}", "$.extra is not allowed by the JSON schema.")]
    public async Task InvalidOutput_PreservesErrorAndReturnsLease(string output, string? schema, string expected)
    {
        var (model, session) = SessionModel();
        session.Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<InferenceResponseFormat>(), It.IsAny<string?>(), It.IsAny<InferenceGenerationOptions?>()))
            .ReturnsAsync(new InferenceResult(output, 2, 1, 3));
        using var provider = CreateProvider(new Mock<ILlamaNative>());
        var request = new PreparedGenerationRequest("hi", new InferenceGenerationOptions(512, 0, 1),
            schema == null ? null : JsonStructuredOutput.Parse(schema));
        var error = await Assert.ThrowsAnyAsync<InferenceException>(() => provider.InferAsync(model.Object, request));
        Assert.Equal(expected, error.Message);
        Assert.IsType(schema == null ? typeof(EmptyInferenceOutputException) : typeof(StructuredOutputException), error);
        session.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData("io", typeof(InferenceException), "Inference failed in the native runtime.")]
    [InlineData("buffer", typeof(OutputBufferExceededException), "Inference output exceeded the configured native buffer size.")]
    [InlineData("empty", typeof(EmptyInferenceOutputException), "Inference returned blank output for a text-generation request.")]
    [InlineData("invalid", typeof(InferenceException), "Inference failed.")]
    [InlineData("cancelled", typeof(OperationCanceledException), "")]
    public async Task NativeFailure_PreservesMappingAndReturnsLease(string failure, Type type, string message)
    {
        var (model, session) = SessionModel();
        NativeException nativeError = failure switch {
            "io" => new NativeIOException("io"), "buffer" => new NativeBufferTooSmallException("buffer"),
            "empty" => new NativeEmptyOutputException("empty"), "cancelled" => new NativeCancelledException("cancelled"),
            _ => new NativeInvalidArgumentException("invalid")
        };
        session.Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<InferenceResponseFormat>(), It.IsAny<string?>(), It.IsAny<InferenceGenerationOptions?>()))
            .ThrowsAsync(nativeError);
        using var provider = CreateProvider(new Mock<ILlamaNative>());
        var error = await Record.ExceptionAsync(() => provider.InferAsync(model.Object,
            new PreparedGenerationRequest("hi", new InferenceGenerationOptions(512, 0, 1))));
        Assert.IsType(type, error);
        if (failure != "cancelled")
        {
            Assert.Equal(message, error!.Message);
        }

        session.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData(6, 2, false)]
    [InlineData(7, 2, true)]
    [InlineData(5, 4, true)]
    public async Task PromptBudget_UsesRequestReservation(int input, int output, bool rejected)
    {
        var (model, session) = SessionModel();
        model.SetupGet(m => m.Metadata).Returns(TestModelFactory.CreateModelMetadata(8));
        var inference = session.Setup(s => s.InferAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(),
            It.IsAny<InferenceResponseFormat>(), It.IsAny<string?>(), It.IsAny<InferenceGenerationOptions?>()));
        if (rejected)
        {
            inference.ThrowsAsync(new NativePromptBudgetExceededException(input));
        }
        else
        {
            inference.ReturnsAsync(new InferenceResult("ok", input, 1, input + 1));
        }

        using var provider = CreateProvider(new Mock<ILlamaNative>());
        var request = new PreparedGenerationRequest("hi", new InferenceGenerationOptions(output, 0, 1));
        if (rejected)
        {
            var error = await Assert.ThrowsAsync<PromptBudgetExceededException>(() => provider.InferAsync(model.Object, request));
            Assert.Contains($"reserved output {output}", error.Message);
            Assert.Contains($"tokenizer reported {input} tokens", error.Message);
        }
        else
        {
            Assert.Equal(input, (await provider.InferAsync(model.Object, request)).InputTokens);
        }

        session.Verify(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        session.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task CountTokens_UsesModelLease()
    {
        var (model, session) = SessionModel();
        session.Setup(s => s.CountTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);
        using var provider = CreateProvider(new Mock<ILlamaNative>());
        Assert.Equal(2, await provider.CountTokensAsync(model.Object, "hi"));
        session.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Unload_WaitsForModelCleanup()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Mock<IEngineModel>();
        model.Setup(m => m.DisposeAsync()).Returns(new ValueTask(gate.Task));
        using var provider = CreateProvider(new Mock<ILlamaNative>());
        var unload = provider.UnloadModelAsync(model.Object);
        Assert.False(unload.IsCompleted);
        gate.SetResult();
        await unload;
        model.Verify(m => m.DisposeAsync(), Times.Once);
    }
}

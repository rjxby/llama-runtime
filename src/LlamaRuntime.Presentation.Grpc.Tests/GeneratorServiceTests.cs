using Grpc.Core;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.ModelHosting;
using LlamaRuntime.Presentation.Grpc.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class GeneratorServiceTests
{
    [Fact]
    public async Task Generate_BlankInferenceOutput_ReturnsInternalError()
    {
        var provider = new Mock<ILlamaProvider>();
        var store = new HostedModelStore();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");
        store.SetLoaded(model);

        var coordinator = new QueuedInferenceCoordinator(
            provider.Object,
            store,
            Options.Create(new InferenceOptions
            {
                ChannelCapacity = 1,
                WorkerCount = 1,
                AcquireTimeout = TimeSpan.FromSeconds(1)
            }),
            NullLogger<QueuedInferenceCoordinator>.Instance);

        provider.Setup(p => p.InferAsync(model, "world", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmptyInferenceOutputException("Inference returned blank output for a text-generation request."));

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var service = new GeneratorService(
                NullLogger<GeneratorService>.Instance,
                store,
                coordinator,
                Options.Create(new LlamaNativeOptions
                {
                    NativeLibraryPath = "test-native",
                    ContextSize = 32,
                    GenerationMaxNewTokens = 8
                }),
                Options.Create(new HostedModelOptions
                {
                    ModelPath = "test-model.gguf",
                    ModelId = "test-model"
                }));

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                service.Generate(
                    new GenerateRequest
                    {
                        RequestId = "blank-output",
                        Prompt = "world"
                    },
                    TestServerCallContext.Create()));

            Assert.Equal(StatusCode.Internal, ex.StatusCode);
            Assert.Equal("Inference returned blank output for a text-generation request.", ex.Status.Detail);
            Assert.Equal(RuntimeErrorMetadata.InferenceFailedCode, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorCodeTrailerName));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Generate_JsonObjectResponseFormat_ReturnsUnsupportedResponseFormatTrailer()
    {
        var (service, coordinator) = await CreateStartedServiceAsync();

        try
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                service.Generate(
                    new GenerateRequest
                    {
                        RequestId = "json-mode",
                        Prompt = "world",
                        ResponseFormat = new ResponseFormat { Type = "json_object" }
                    },
                    TestServerCallContext.Create()));

            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
            Assert.Equal(RuntimeErrorMetadata.UnsupportedResponseFormatCode, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorCodeTrailerName));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Generate_NonDefaultGenerationOverride_ReturnsInvalidArgumentTrailer()
    {
        var (service, coordinator) = await CreateStartedServiceAsync();

        try
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                service.Generate(
                    new GenerateRequest
                    {
                        RequestId = "override",
                        Prompt = "world",
                        Generation = new GenerationOptions { Temperature = 0.3f }
                    },
                    TestServerCallContext.Create()));

            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
            Assert.Equal(RuntimeErrorMetadata.InvalidArgumentCode, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorCodeTrailerName));
            Assert.Contains("generation overrides", ex.Status.Detail);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Generate_AtomicInferenceFailure_ReturnsInternalErrorTrailer()
    {
        var provider = new Mock<ILlamaProvider>();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");
        provider.Setup(p => p.InferAsync(model, "world", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InferenceException("count failed"));

        var (service, coordinator) = await CreateStartedServiceAsync(provider: provider, model: model);

        try
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                service.Generate(
                    new GenerateRequest
                    {
                        RequestId = "usage-fail",
                        Prompt = "world"
                    },
                    TestServerCallContext.Create()));

            Assert.Equal(StatusCode.Internal, ex.StatusCode);
            Assert.Equal(RuntimeErrorMetadata.InferenceFailedCode, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorCodeTrailerName));
            Assert.Equal("count failed", ex.Status.Detail);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetCapabilities_UsesConfiguredModelId()
    {
        var service = CreateService(
            hostedModelOptions: new HostedModelOptions
            {
                ModelPath = "models/test-model.gguf",
                ModelId = "public-model"
            });

        var reply = await service.GetCapabilities(new GetCapabilitiesRequest(), TestServerCallContext.Create());

        Assert.Equal("public-model", reply.ModelId);
        Assert.False(reply.SupportsStructuredOutput);
        Assert.False(reply.SupportsJsonObjectOutput);
        Assert.False(reply.SupportsSpeculativeDecoding);
        Assert.Equal("llama_cpp", reply.TokenizerFamily);
    }

    [Fact]
    public async Task GetCapabilities_UsesRequiredModelId()
    {
        var service = CreateService(
            hostedModelOptions: new HostedModelOptions
            {
                ModelPath = "/tmp/models/fallback-model.gguf",
                ModelId = "required-model-id"
            });

        var reply = await service.GetCapabilities(new GetCapabilitiesRequest(), TestServerCallContext.Create());

        Assert.Equal("required-model-id", reply.ModelId);
    }

    [Theory]
    [InlineData("prompt_budget_exceeded", StatusCode.InvalidArgument, RuntimeErrorMetadata.PromptBudgetExceededCode, "Prompt exceeds input budget")]
    [InlineData("output_buffer_exceeded", StatusCode.ResourceExhausted, RuntimeErrorMetadata.OutputBufferExceededCode, "Buffer too small")]
    [InlineData("inference_failed", StatusCode.Internal, RuntimeErrorMetadata.InferenceFailedCode, "Inference failed")]
    public async Task Generate_ProviderFailures_ReturnNormalizedTrailers(
        string scenario,
        StatusCode expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        var provider = new Mock<ILlamaProvider>();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");

        var (service, coordinator) = await CreateStartedServiceAsync(provider: provider, model: model);
        provider.Setup(p => p.InferAsync(model, "world", It.IsAny<CancellationToken>()))
            .ThrowsAsync(scenario switch
            {
                "prompt_budget_exceeded" => new PromptBudgetExceededException("Prompt exceeds input budget"),
                "output_buffer_exceeded" => new OutputBufferExceededException("Buffer too small"),
                _ => new InferenceException("Inference failed")
            });

        try
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                service.Generate(
                    new GenerateRequest
                    {
                        RequestId = scenario,
                        Prompt = "world"
                    },
                    TestServerCallContext.Create()));

            Assert.Equal(expectedStatus, ex.StatusCode);
            Assert.Equal(expectedCode, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorCodeTrailerName));
            Assert.Equal(expectedMessage, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorMessageTrailerName));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Generate_ModelUnavailable_ReturnsNormalizedTrailers()
    {
        var service = CreateService(loadModel: false);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.Generate(
                new GenerateRequest
                {
                    RequestId = "unavailable",
                    Prompt = "world"
                },
                TestServerCallContext.Create()));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Equal(RuntimeErrorMetadata.ModelUnavailableCode, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorCodeTrailerName));
    }

    [Fact]
    public async Task Generate_QueueRejected_ReturnsNormalizedTrailers()
    {
        var (service, coordinator) = await CreateStartedServiceAsync();
        await coordinator.StopAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.Generate(
                new GenerateRequest
                {
                    RequestId = "queue-rejected",
                    Prompt = "world"
                },
                TestServerCallContext.Create()));

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Equal(RuntimeErrorMetadata.QueueRejectedCode, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorCodeTrailerName));
    }

    [Fact]
    public async Task Generate_Cancelled_ReturnsNormalizedTrailers()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");

        provider.Setup(p => p.InferAsync(model, "world", It.IsAny<CancellationToken>()))
            .Returns(async (IEngineModel _, string _, CancellationToken ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return CreateInferenceResult("unreachable");
            });

        var (service, coordinator) = await CreateStartedServiceAsync(provider, model);
        using var cts = new CancellationTokenSource();

        try
        {
            var generateTask = service.Generate(
                new GenerateRequest
                {
                    RequestId = "cancelled",
                    Prompt = "world"
                },
                TestServerCallContext.Create(cts.Token));

            await started.Task;
            await cts.CancelAsync();

            var ex = await Assert.ThrowsAsync<RpcException>(() => generateTask);

            Assert.Equal(StatusCode.Cancelled, ex.StatusCode);
            Assert.Equal(RuntimeErrorMetadata.CancelledCode, GetTrailerValue(ex, RuntimeErrorMetadata.ErrorCodeTrailerName));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Generate_ReturnsUsageFromAtomicInference()
    {
        var (service, coordinator) = await CreateStartedServiceAsync();

        try
        {
            var reply = await service.Generate(
                new GenerateRequest
                {
                    RequestId = "usage",
                    Prompt = "world"
                },
                TestServerCallContext.Create());

            Assert.Equal("mocked response", reply.Content);
            Assert.Equal(5, reply.Usage.InputTokens);
            Assert.Equal("mocked response".Length, reply.Usage.OutputTokens);
            Assert.Equal(5 + "mocked response".Length, reply.Usage.TotalTokens);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<(GeneratorService Service, QueuedInferenceCoordinator Coordinator)> CreateStartedServiceAsync(
        Mock<ILlamaProvider>? provider = null,
        IEngineModel? model = null)
    {
        var coordinator = CreateCoordinator(provider, model, out var store);
        await coordinator.StartAsync(CancellationToken.None);
        return (CreateService(store, coordinator), coordinator);
    }

    private static GeneratorService CreateService(
        HostedModelStore? store = null,
        QueuedInferenceCoordinator? coordinator = null,
        bool loadModel = true,
        HostedModelOptions? hostedModelOptions = null)
    {
        store ??= new HostedModelStore();
        if (loadModel && !store.TryGetLoadedModel(out _))
        {
            store.SetLoaded(Mock.Of<IEngineModel>(m => m.Id == "model"));
        }

        if (coordinator is null)
        {
            coordinator = CreateCoordinator(null, loadModel ? store.GetSnapshot().Model : null, out var coordinatorStore, loadModel);
            store = coordinatorStore;
        }

        return new GeneratorService(
            NullLogger<GeneratorService>.Instance,
            store,
            coordinator,
            Options.Create(new LlamaNativeOptions
            {
                NativeLibraryPath = "test-native",
                ContextSize = 32,
                GenerationMaxNewTokens = 8
            }),
            Options.Create(hostedModelOptions ?? new HostedModelOptions
            {
                ModelPath = "test-model.gguf",
                ModelId = "test-model"
            }));
    }

    private static QueuedInferenceCoordinator CreateCoordinator(
        Mock<ILlamaProvider>? provider,
        IEngineModel? model,
        out HostedModelStore store,
        bool loadModel = true)
    {
        var useDefaultProviderBehavior = provider is null;
        provider ??= new Mock<ILlamaProvider>();
        store = new HostedModelStore();
        model ??= Mock.Of<IEngineModel>(m => m.Id == "model");
        if (loadModel)
        {
            store.SetLoaded(model);
        }

        if (useDefaultProviderBehavior)
        {
            provider.Setup(p => p.InferAsync(model, "world", It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateInferenceResult("mocked response"));
            provider.Setup(p => p.CountTokensAsync(model, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEngineModel _, string prompt, CancellationToken _) => prompt.Length);
        }

        return new QueuedInferenceCoordinator(
            provider.Object,
            store,
            Options.Create(new InferenceOptions
            {
                ChannelCapacity = 1,
                WorkerCount = 1,
                AcquireTimeout = TimeSpan.FromSeconds(1)
            }),
            NullLogger<QueuedInferenceCoordinator>.Instance);
    }

    private static string? GetTrailerValue(RpcException exception, string key) =>
        exception.Trailers.SingleOrDefault(entry => entry.Key == key)?.Value;

    private static InferenceResult CreateInferenceResult(string content) =>
        new(
            content,
            5,
            content.Length,
            5 + content.Length);
}

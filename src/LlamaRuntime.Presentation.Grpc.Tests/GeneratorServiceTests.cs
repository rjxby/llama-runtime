using Grpc.Core;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Presentation.Grpc.Inference;
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
        var coordinator = CreateCoordinator();
        coordinator.Setup(c => c.InferAsync("world", It.IsAny<CancellationToken>(), "blank-output"))
            .ThrowsAsync(new EmptyInferenceOutputException("Inference returned blank output for a text-generation request."));

        var service = CreateService(coordinator);

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

    [Fact]
    public async Task Generate_JsonObjectResponseFormat_ReturnsUnsupportedResponseFormatTrailer()
    {
        var service = CreateService();

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

    [Fact]
    public async Task Generate_NonDefaultGenerationOverride_ReturnsInvalidArgumentTrailer()
    {
        var service = CreateService();

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

    [Fact]
    public async Task Generate_AtomicInferenceFailure_ReturnsInternalErrorTrailer()
    {
        var coordinator = CreateCoordinator();
        coordinator.Setup(c => c.InferAsync("world", It.IsAny<CancellationToken>(), "usage-fail"))
            .ThrowsAsync(new InferenceException("count failed"));

        var service = CreateService(coordinator);

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

    [Fact]
    public async Task GetCapabilities_UsesConfiguredModelId()
    {
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel("/tmp/models/actual.gguf"), "public-model");
        var service = CreateService(hostedModel: hostedModel);

        var reply = await service.GetCapabilities(new GetCapabilitiesRequest(), TestServerCallContext.Create());

        Assert.Equal("public-model", reply.ModelId);
        Assert.False(reply.SupportsStructuredOutput);
        Assert.False(reply.SupportsJsonObjectOutput);
        Assert.False(reply.SupportsSpeculativeDecoding);
        Assert.Equal("sentencepiece", reply.TokenizerFamily);
    }

    [Fact]
    public async Task GetCapabilities_FallsBackToSanitizedSourceName()
    {
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel("/tmp/models/required.gguf"));
        var service = CreateService(hostedModel: hostedModel);

        var reply = await service.GetCapabilities(new GetCapabilitiesRequest(), TestServerCallContext.Create());

        Assert.Equal("required", reply.ModelId);
    }

    [Fact]
    public async Task EstimateTokens_UsesLoadedModelEffectiveContext()
    {
        var coordinator = CreateCoordinator();
        coordinator.Setup(c => c.CountTokensAsync("hello", It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(5);

        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel(contextSize: 16), "test-model");
        var service = CreateService(coordinator, hostedModel);

        var reply = await service.EstimateTokens(
            new EstimateTokensRequest
            {
                Prompt = "hello"
            },
            TestServerCallContext.Create());

        Assert.Equal(5, reply.TokenCount);
        Assert.Equal(16, reply.ContextSize);
        Assert.Equal(8, reply.ReservedOutputTokens);
        Assert.Equal(8, reply.MaxAllowedInputTokens);
    }

    [Fact]
    public async Task Generate_MaxOutputTokensValidation_UsesLoadedModelEffectiveContext()
    {
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel(contextSize: 16), "test-model");
        var service = CreateService(hostedModel: hostedModel);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.Generate(
                new GenerateRequest
                {
                    RequestId = "max-output",
                    Prompt = "world",
                    Generation = new GenerationOptions
                    {
                        MaxOutputTokens = 16
                    }
                },
                TestServerCallContext.Create()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("effective context size 16", ex.Status.Detail);
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
        var coordinator = CreateCoordinator();
        coordinator.Setup(c => c.InferAsync("world", It.IsAny<CancellationToken>(), scenario))
            .ThrowsAsync(scenario switch
            {
                "prompt_budget_exceeded" => new PromptBudgetExceededException("Prompt exceeds input budget"),
                "output_buffer_exceeded" => new OutputBufferExceededException("Buffer too small"),
                _ => new InferenceException("Inference failed")
            });

        var service = CreateService(coordinator);

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
        var coordinator = CreateCoordinator();
        coordinator.Setup(c => c.InferAsync("world", It.IsAny<CancellationToken>(), "queue-rejected"))
            .ThrowsAsync(new InferenceQueueRejectedException("Inference queue is closed because the runtime is stopping."));

        var service = CreateService(coordinator);

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
        var coordinator = CreateCoordinator();
        coordinator.Setup(c => c.InferAsync("world", It.IsAny<CancellationToken>(), "cancelled"))
            .Returns(async (string _, CancellationToken ct, string? _) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return CreateInferenceResult("unreachable");
            });

        var service = CreateService(coordinator);
        using var cts = new CancellationTokenSource();

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

    [Fact]
    public async Task Generate_ReturnsUsageFromAtomicInference()
    {
        var service = CreateService();

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

    [Fact]
    public async Task Generate_FallsBackToSanitizedSourceName_WhenConfiguredModelIdMissing()
    {
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel("/tmp/models/fallback.gguf"));
        var service = CreateService(hostedModel: hostedModel);

        var reply = await service.Generate(
            new GenerateRequest
            {
                RequestId = "fallback-id",
                Prompt = "world"
            },
            TestServerCallContext.Create());

        Assert.Equal("fallback", reply.Model);
    }

    private static GeneratorService CreateService(
        Mock<IInferenceCoordinator>? coordinator = null,
        HostedModel? hostedModel = null,
        bool loadModel = true)
    {
        hostedModel ??= CreateHostedModel();
        if (loadModel && !hostedModel.TryGetLoadedModel(out _))
        {
            hostedModel.SetLoaded(CreateModel(), "test-model");
        }

        coordinator ??= CreateCoordinator();

        return new GeneratorService(
            NullLogger<GeneratorService>.Instance,
            coordinator.Object,
            TestModelFactory.CreateNativeOptions(),
            hostedModel);
    }

    private static Mock<IInferenceCoordinator> CreateCoordinator()
    {
        var coordinator = new Mock<IInferenceCoordinator>();
        coordinator.Setup(c => c.InferAsync("world", It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(CreateInferenceResult("mocked response"));
        coordinator.Setup(c => c.CountTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((string prompt, CancellationToken _, string? _) => prompt.Length);
        return coordinator;
    }

    private static string? GetTrailerValue(RpcException exception, string key) =>
        exception.Trailers.SingleOrDefault(entry => entry.Key == key)?.Value;

    private static HostedModel CreateHostedModel() =>
        new(TestModelFactory.CreateNativeOptions());

    private static IEngineModel CreateModel(
        string sourcePath = "model.gguf",
        int contextSize = 32) =>
        TestModelFactory.CreateEngineModel(sourcePath, contextSize);

    private static InferenceResult CreateInferenceResult(string content) =>
        new(
            content,
            5,
            content.Length,
            5 + content.Length);
}

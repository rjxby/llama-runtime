using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.HealthChecks;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.Inference;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed partial class QueuedInferenceCoordinatorTests
{
    [Fact]
    public async Task Worker_LogsEnumOperationNames_ForStartedWorkItems()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();
        var logger = new ListLogger<QueuedInferenceWorker>();
        hostedModel.SetLoaded(model);

        provider.Setup(p => p.InferAsync(model, "prompt", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ReturnsAsync(new InferenceResult("ok", 5, 2, 7));
        provider.Setup(p => p.CountTokensAsync(model, "prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel, logger: logger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await coordinator.InferAsync("prompt", CancellationToken.None, "infer-request");
            await coordinator.CountTokensAsync("prompt", CancellationToken.None, "count-request");

            Assert.Contains(logger.Messages, message => message.Contains("operation=Infer", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, message => message.Contains("operation=CountTokens", StringComparison.Ordinal));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InferAsync_WithResponseFormat_ForwardsStructuredOptionToProvider()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();
        hostedModel.SetLoaded(model);

        provider.Setup(p => p.InferAsync(model, "prompt", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json, ""))
            .ReturnsAsync(new InferenceResult("""{"ok":true}""", 5, 4, 9));

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var result = await coordinator.InferAsync(
                "prompt",
                CancellationToken.None,
                "json-request",
                InferenceResponseFormat.Json,
                "");

            Assert.Equal("""{"ok":true}""", result.Content);
            provider.Verify(
                p => p.InferAsync(model, "prompt", It.IsAny<CancellationToken>(), InferenceResponseFormat.Json, ""),
                Times.Once);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InferAsync_WithGenerationOptions_ForwardsOptionsToProvider()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();
        var generationOptions = new InferenceGenerationOptions(4, 0.7f, 0.8f);
        hostedModel.SetLoaded(model);

        provider.Setup(p => p.InferAsync(model, "prompt", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text, null, generationOptions))
            .ReturnsAsync(new InferenceResult("ok", 5, 4, 9));

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var result = await coordinator.InferAsync(
                "prompt",
                CancellationToken.None,
                "generation-request",
                InferenceResponseFormat.Text,
                null,
                generationOptions);

            Assert.Equal("ok", result.Content);
            provider.Verify(
                p => p.InferAsync(model, "prompt", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text, null, generationOptions),
                Times.Once);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InferAsync_WhenQueueIsSaturated_ThrowsInferenceQueueRejectedException()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel());

        provider.Setup(p => p.InferAsync(It.IsAny<IEngineModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .Returns(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                return new InferenceResult("ok", 5, 2, 7);
            });

        var (coordinator, worker) = CreateHarness(
            provider.Object,
            hostedModel,
            options: new InferenceOptions
            {
                ChannelCapacity = 1,
                WorkerCount = 1,
                AcquireTimeout = TimeSpan.FromMilliseconds(100),
                StartupWarmupPrompt = "Hello"
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var inFlight = coordinator.InferAsync("first", CancellationToken.None);
            var queued = coordinator.InferAsync("second", CancellationToken.None);

            var ex = await Assert.ThrowsAsync<InferenceQueueRejectedException>(() =>
                coordinator.InferAsync("third", CancellationToken.None));

            Assert.Contains("did not accept", ex.Message);

            gate.TrySetResult();
            await Task.WhenAll(inFlight, queued);
        }
        finally
        {
            gate.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(HostedModelState.NotLoaded, "Model not loaded.")]
    [InlineData(HostedModelState.Loading, "Model is still loading.")]
    [InlineData(HostedModelState.WarmingUp, "Model is warming up.")]
    [InlineData(HostedModelState.Stopping, "Model is stopping.")]
    public async Task InferAsync_WhenModelUnavailable_ThrowsExpectedMessage(
        HostedModelState state,
        string expectedMessage)
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        SetHostedModelState(hostedModel, state);
        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var ex = await Assert.ThrowsAsync<ModelNotFoundException>(() =>
                coordinator.InferAsync("prompt", CancellationToken.None));

            Assert.Equal(expectedMessage, ex.Message);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InferAsync_WhenModelFailed_ThrowsFailureMessage()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        hostedModel.SetFailed(new InvalidOperationException("boom"));
        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var ex = await Assert.ThrowsAsync<ModelNotFoundException>(() =>
                coordinator.InferAsync("prompt", CancellationToken.None));

            Assert.Equal("boom", ex.Message);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InferAsync_WhenCallerCancelledBeforeExecution_IsCancelled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();
        hostedModel.SetLoaded(model);

        provider.Setup(p => p.InferAsync(model, "first", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .Returns(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                return new InferenceResult("ok", 5, 2, 7);
            });
        provider.Setup(p => p.InferAsync(model, "second", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ReturnsAsync(new InferenceResult("late", 5, 4, 9));

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var inFlight = coordinator.InferAsync("first", CancellationToken.None);
            using var cts = new CancellationTokenSource();
            var queuedTask = coordinator.InferAsync("second", cts.Token);

            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedTask);

            gate.TrySetResult();
            await inFlight;
        }
        finally
        {
            gate.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InferAsync_WhenCallerCancelledDuringExecution_IsCancelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();
        hostedModel.SetLoaded(model);

        provider.Setup(p => p.InferAsync(model, "prompt", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text, null, null))
            .Returns(async (IEngineModel _, string _, CancellationToken ct, InferenceResponseFormat _, string? _, InferenceGenerationOptions? _) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new InferenceResult("unreachable", 5, 11, 16);
            });

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource();
            var task = coordinator.InferAsync("prompt", cts.Token);

            await started.Task;
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InferAsync_WhenWorkerStopped_ThrowsInferenceQueueRejectedException()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel());
        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InferenceQueueRejectedException>(() =>
            coordinator.InferAsync("prompt", CancellationToken.None));

        Assert.Contains("queue is closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelLoaderWorker_Warms_Model_Before_Marking_It_Loaded()
    {
        var warmupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWarmupToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .Returns(async () =>
            {
                warmupStarted.TrySetResult();
                await allowWarmupToFinish.Task;
                return new InferenceResult("warmed", 5, 6, 11);
            });
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider.Object,
            hostedModel,
            hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "model" }),
            TestModelFactory.CreateNativeOptions(),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        var startTask = worker.StartAsync(CancellationToken.None);
        await warmupStarted.Task;

        var warmingSnapshot = hostedModel.GetSnapshot();
        Assert.Equal(HostedModelState.WarmingUp, warmingSnapshot.State);
        Assert.Same(model, warmingSnapshot.Model);

        var check = new ModelReadyHealthCheck(hostedModel);
        var warming = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, warming.Status);

        allowWarmupToFinish.TrySetResult();
        await startTask;

        var loadedSnapshot = hostedModel.GetSnapshot();
        Assert.Equal(HostedModelState.Loaded, loadedSnapshot.State);
        Assert.Same(model, loadedSnapshot.Model);

        await worker.StopAsync(CancellationToken.None);

        var stoppedSnapshot = hostedModel.GetSnapshot();
        Assert.Equal(HostedModelState.NotLoaded, stoppedSnapshot.State);
        Assert.Null(stoppedSnapshot.Model);
        provider.Verify(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModelLoaderWorker_WarmupFailure_Fails_Startup_And_Unloads_Model()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ThrowsAsync(new InferenceException("warmup failed"));
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider.Object,
            hostedModel,
            hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "model" }),
            TestModelFactory.CreateNativeOptions(),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => worker.StartAsync(CancellationToken.None));

        Assert.Equal("warmup failed", ex.Message);
        Assert.Equal(HostedModelState.Failed, hostedModel.GetSnapshot().State);
        provider.Verify(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModelLoaderWorker_BlankWarmupOutput_Fails_Startup_And_Unloads_Model()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ThrowsAsync(new EmptyInferenceOutputException("Inference returned blank output for a text-generation request."));
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider.Object,
            hostedModel,
            hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "model" }),
            TestModelFactory.CreateNativeOptions(),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        var ex = await Assert.ThrowsAsync<EmptyInferenceOutputException>(() => worker.StartAsync(CancellationToken.None));

        Assert.Equal("Inference returned blank output for a text-generation request.", ex.Message);
        Assert.Equal(HostedModelState.Failed, hostedModel.GetSnapshot().State);
        provider.Verify(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModelLoaderWorker_CancelledWarmup_UnloadsModelWithNonCancelledCleanupToken()
    {
        using var cts = new CancellationTokenSource();
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromException<InferenceResult>(new OperationCanceledException(cts.Token));
            });
        provider.Setup(p => p.UnloadModelAsync(model, It.Is<CancellationToken>(token => !token.IsCancellationRequested)))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider.Object,
            hostedModel,
            hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "model" }),
            TestModelFactory.CreateNativeOptions(),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        await Assert.ThrowsAsync<OperationCanceledException>(() => worker.StartAsync(cts.Token));

        provider.Verify(
            p => p.UnloadModelAsync(model, It.Is<CancellationToken>(token => !token.IsCancellationRequested)),
            Times.Once);
    }

    [Fact]
    public async Task ModelLoaderWorker_ConfiguredContextMismatch_FailsStartup()
    {
        var native = new Mock<ILlamaNative>();
        var contextManager = new Mock<ILlamaContextManager>();
        var hostedModel = CreateHostedModel();

        native.Setup(n => n.LoadModel("model.gguf"))
            .Returns(LlamaModelHandle.FromIntPtr(new IntPtr(1)));
        native.Setup(n => n.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
            .Returns(new NativeModelMetadata(16, NativeTokenizerType.SentencePiece));
        native.Setup(n => n.CreateContext(It.IsAny<LlamaModelHandle>()))
            .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
            .Returns(new NativeContextMetadata(16));

        using var provider = new LlamaProvider(
            native.Object,
            contextManager.Object,
            TestModelFactory.CreateNativeOptions(),
            NullLogger<LlamaProvider>.Instance);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider,
            hostedModel,
            hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "public-model" }),
            TestModelFactory.CreateNativeOptions(),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        var ex = await Assert.ThrowsAsync<ModelLoadException>(() => worker.StartAsync(CancellationToken.None));

        Assert.Contains("Configured context size 32 does not match actual created context size 16", ex.Message);
        Assert.Equal(HostedModelState.Failed, hostedModel.GetSnapshot().State);
    }

    [Fact]
    public async Task ModelReadyHealthCheck_ReportsLoadingWarmingUpAndFailedStates()
    {
        var hostedModel = CreateHostedModel();
        var check = new ModelReadyHealthCheck(hostedModel);

        hostedModel.SetLoading();
        var loading = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, loading.Status);

        hostedModel.SetWarmingUp(CreateModel());
        var warmingUp = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, warmingUp.Status);

        hostedModel.SetFailed(new InvalidOperationException("boom"));
        var failed = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, failed.Status);
        Assert.Contains("boom", failed.Description);
    }

    [Fact]
    public async Task ModelLoaderWorker_LogsCapabilityDiagnostics_AfterWarmup()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel("model.gguf");
        var logger = new ListLogger<ModelLoaderWorker>();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ReturnsAsync(new InferenceResult("warmed", 5, 6, 11));
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            logger,
            provider.Object,
            hostedModel,
            hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "public-model" }),
            TestModelFactory.CreateNativeOptions(),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        await worker.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Messages, message => message.Contains("Loaded model metadata for model public-model", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("configured_runtime_context_size=32", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("effective_runtime_context_size=32", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("Runtime capabilities for model public-model", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("structured_output", StringComparison.Ordinal) && message.Contains("unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("json_output", StringComparison.Ordinal) && message.Contains("unavailable", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("speculative_decoding", StringComparison.Ordinal) && message.Contains("not implemented", StringComparison.Ordinal));

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ModelLoaderWorker_LogsSanitizedFallbackModelId_WhenConfiguredModelIdMissing()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel("/tmp/models/fallback.gguf");
        var logger = new ListLogger<ModelLoaderWorker>();

        provider.Setup(p => p.LoadModelAsync("/tmp/models/fallback.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>(), InferenceResponseFormat.Text))
            .ReturnsAsync(new InferenceResult("warmed", 5, 6, 11));
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            logger,
            provider.Object,
            hostedModel,
            hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "/tmp/models/fallback.gguf", ModelId = string.Empty }),
            TestModelFactory.CreateNativeOptions(),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        await worker.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Messages, message => message.Contains("Loaded model metadata for model fallback", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("Runtime capabilities for model fallback", StringComparison.Ordinal));

        await worker.StopAsync(CancellationToken.None);
    }

    private static (QueuedInferenceCoordinator Coordinator, QueuedInferenceWorker Worker) CreateHarness(
        ILlamaProvider provider,
        HostedModel hostedModel,
        InferenceOptions? options = null,
        ILogger<QueuedInferenceWorker>? logger = null)
    {
        options ??= new InferenceOptions
        {
            ChannelCapacity = 1,
            WorkerCount = 1,
            AcquireTimeout = TimeSpan.FromSeconds(1),
            StartupWarmupPrompt = "Hello"
        };

        var queue = new InferenceWorkQueue(Options.Create(options));
        var coordinator = new QueuedInferenceCoordinator(provider, queue);
        var worker = new QueuedInferenceWorker(
            queue,
            hostedModel,
            Options.Create(options),
            logger ?? NullLogger<QueuedInferenceWorker>.Instance);

        return (coordinator, worker);
    }

    private static void SetHostedModelState(HostedModel hostedModel, HostedModelState state)
    {
        switch (state)
        {
            case HostedModelState.NotLoaded:
                hostedModel.Reset();
                break;
            case HostedModelState.Loading:
                hostedModel.SetLoading();
                break;
            case HostedModelState.WarmingUp:
                hostedModel.SetWarmingUp(CreateModel());
                break;
            case HostedModelState.Stopping:
                hostedModel.SetStopping();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    private static HostedModel CreateHostedModel() =>
        new(TestModelFactory.CreateNativeOptions());

    private static IEngineModel CreateModel(
        string sourcePath = "model.gguf",
        int contextSize = 32) =>
        TestModelFactory.CreateEngineModel(sourcePath, contextSize);
}

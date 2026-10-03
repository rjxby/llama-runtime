using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
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

        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "prompt" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("ok", 5, 2, 7));
        provider.Setup(p => p.CountTokensAsync(model, "prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel, logger: logger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await coordinator.InferAsync(new PreparedGenerationRequest("prompt", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, "infer-request");
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

        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "prompt" && r.Constraint != null && r.Constraint!.Grammar == JsonStructuredOutput.Parse("").Grammar), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("""{"ok":true}""", 5, 4, 9));

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var result = await coordinator.InferAsync(new PreparedGenerationRequest("prompt", new InferenceGenerationOptions(8, 0, 1), JsonStructuredOutput.Parse("")), CancellationToken.None, "json-request");

            Assert.Equal("""{"ok":true}""", result.Content);
            provider.Verify(
                p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "prompt" && r.Constraint != null && r.Constraint!.Grammar == JsonStructuredOutput.Parse("").Grammar), It.IsAny<CancellationToken>()),
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

        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "prompt" && r.Constraint == null && (r.Generation == generationOptions)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("ok", 5, 4, 9));

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var result = await coordinator.InferAsync(new PreparedGenerationRequest("prompt", generationOptions), CancellationToken.None, "generation-request");

            Assert.Equal("ok", result.Content);
            provider.Verify(
                p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "prompt" && r.Constraint == null && (r.Generation == generationOptions)), It.IsAny<CancellationToken>()),
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

        provider.Setup(p => p.InferAsync(It.IsAny<IEngineModel>(), It.Is<PreparedGenerationRequest>(r => r.Constraint == null), It.IsAny<CancellationToken>()))
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
            var inFlight = coordinator.InferAsync(new PreparedGenerationRequest("first", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null);
            var queued = coordinator.InferAsync(new PreparedGenerationRequest("second", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null);

            var ex = await Assert.ThrowsAsync<InferenceQueueRejectedException>(() =>
                coordinator.InferAsync(new PreparedGenerationRequest("third", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null));

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
        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        SetHostedModelState(hostedModel, state);
        try
        {
            var ex = await Assert.ThrowsAsync<ModelNotFoundException>(() =>
                coordinator.InferAsync(new PreparedGenerationRequest("prompt", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null));

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
        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        hostedModel.SetFailed(new InvalidOperationException("boom"));
        try
        {
            var ex = await Assert.ThrowsAsync<ModelNotFoundException>(() =>
                coordinator.InferAsync(new PreparedGenerationRequest("prompt", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null));

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

        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "first" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                return new InferenceResult("ok", 5, 2, 7);
            });
        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "second" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("late", 5, 4, 9));

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var inFlight = coordinator.InferAsync(new PreparedGenerationRequest("first", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null);
            using var cts = new CancellationTokenSource();
            var queuedTask = coordinator.InferAsync(new PreparedGenerationRequest("second", new InferenceGenerationOptions(8, 0, 1)), cts.Token, null);

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

        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "prompt" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .Returns(async (IEngineModel _, PreparedGenerationRequest _, CancellationToken ct) =>
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
            var task = coordinator.InferAsync(new PreparedGenerationRequest("prompt", new InferenceGenerationOptions(8, 0, 1)), cts.Token, null);

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
            coordinator.InferAsync(new PreparedGenerationRequest("prompt", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null));

        Assert.Contains("queue is closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InferenceWorker_Warms_Model_Before_Marking_It_Loaded()
    {
        var warmupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWarmupToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "Hello" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                warmupStarted.TrySetResult();
                await allowWarmupToFinish.Task;
                return new InferenceResult("warmed", 5, 6, 11);
            });
        provider.Setup(p => p.CountTokensAsync(model, "started", It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var queue = new InferenceWorkQueue(Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));
        var worker = new QueuedInferenceWorker(
            queue, hostedModel, Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }),
            NullLogger<QueuedInferenceWorker>.Instance, Mock.Of<IHostApplicationLifetime>(), provider.Object, hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "model" }), TestModelFactory.CreateNativeOptions());

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

        var coordinator = new QueuedInferenceCoordinator(provider.Object, queue);
        await coordinator.CountTokensAsync("started", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        var stoppedSnapshot = hostedModel.GetSnapshot();
        Assert.Equal(HostedModelState.NotLoaded, stoppedSnapshot.State);
        Assert.Null(stoppedSnapshot.Model);
        Mock.Get(model).Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task InferenceWorker_WarmupFailure_Fails_Startup_And_Unloads_Model()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "Hello" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InferenceException("warmup failed"));

        var worker = new QueuedInferenceWorker(
            new InferenceWorkQueue(Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" })), hostedModel, Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }),
            NullLogger<QueuedInferenceWorker>.Instance, Mock.Of<IHostApplicationLifetime>(), provider.Object, hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "model" }), TestModelFactory.CreateNativeOptions());

        var ex = await Assert.ThrowsAsync<InferenceException>(() => worker.StartAsync(CancellationToken.None));

        Assert.Equal("warmup failed", ex.Message);
        Assert.Equal(HostedModelState.Failed, hostedModel.GetSnapshot().State);
        Mock.Get(model).Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task InferenceWorker_BlankWarmupOutput_Fails_Startup_And_Unloads_Model()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "Hello" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmptyInferenceOutputException("Inference returned blank output for a text-generation request."));

        var worker = new QueuedInferenceWorker(
            new InferenceWorkQueue(Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" })), hostedModel, Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }),
            NullLogger<QueuedInferenceWorker>.Instance, Mock.Of<IHostApplicationLifetime>(), provider.Object, hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "model" }), TestModelFactory.CreateNativeOptions());

        var ex = await Assert.ThrowsAsync<EmptyInferenceOutputException>(() => worker.StartAsync(CancellationToken.None));

        Assert.Equal("Inference returned blank output for a text-generation request.", ex.Message);
        Assert.Equal(HostedModelState.Failed, hostedModel.GetSnapshot().State);
        Mock.Get(model).Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task InferenceWorker_CancelledWarmup_UnloadsModelWithNonCancelledCleanupToken()
    {
        using var cts = new CancellationTokenSource();
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "Hello" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromException<InferenceResult>(new OperationCanceledException(cts.Token));
            });

        var worker = new QueuedInferenceWorker(
            new InferenceWorkQueue(Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" })), hostedModel, Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }),
            NullLogger<QueuedInferenceWorker>.Instance, Mock.Of<IHostApplicationLifetime>(), provider.Object, hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "model" }), TestModelFactory.CreateNativeOptions());

        await Assert.ThrowsAsync<OperationCanceledException>(() => worker.StartAsync(cts.Token));

        Mock.Get(model).Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task InferenceWorker_ShutdownObservesSharedCleanupFailure()
    {
        var provider = new Mock<ILlamaProvider>();
        using var hostedModel = CreateHostedModel();
        var model = CreateModel();
        var failure = new IOException("model cleanup failed");
        Mock.Get(model).Setup(m => m.DisposeAsync()).Returns(() => new ValueTask(Task.FromException(failure)));
        provider.Setup(p => p.CountTokensAsync(model, "started", It.IsAny<CancellationToken>())).ReturnsAsync(1);
        hostedModel.SetLoaded(model);
        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel);
        using var workerLifetime = worker;
        await worker.StartAsync(CancellationToken.None);
        await coordinator.CountTokensAsync("started", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => worker.ExecuteTask!));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => hostedModel.CloseAsync()));
        Mock.Get(model).Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task InferenceWorker_LoadFinishingAfterDisposal_ReleasesRejectedModel()
    {
        var provider = new Mock<ILlamaProvider>();
        using var hostedModel = CreateHostedModel();
        var model = CreateModel();
        var loaded = new TaskCompletionSource<IEngineModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, worker) = CreateHarness(provider.Object, hostedModel);
        using var workerLifetime = worker;
        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .Returns(loaded.Task);

        var startup = worker.StartAsync(CancellationToken.None);
        Assert.False(startup.IsCompleted);
        Assert.Equal(HostedModelState.Loading, hostedModel.GetSnapshot().State);
        hostedModel.Dispose();
        worker.Dispose();
        loaded.SetResult(model);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => startup.WaitAsync(TimeSpan.FromSeconds(2)));
        Mock.Get(model).Verify(m => m.DisposeAsync(), Times.Once);
        Assert.Equal(HostedModelState.Failed, hostedModel.GetSnapshot().State);
        provider.Verify(p => p.InferAsync(It.IsAny<IEngineModel>(), It.IsAny<PreparedGenerationRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InferenceWorker_ConfiguredContextMismatch_FailsStartup()
    {
        var native = new Mock<ILlamaNative>();
        var modelMock = new Mock<IEngineModel>();
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
            Options.Create(new InferenceOptions { WorkerCount = 1 }),
            TestModelFactory.CreateNativeOptions(),
            NullLogger<LlamaProvider>.Instance);

        var worker = new QueuedInferenceWorker(
            new InferenceWorkQueue(Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" })), hostedModel, Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }),
            NullLogger<QueuedInferenceWorker>.Instance, Mock.Of<IHostApplicationLifetime>(), provider, hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "public-model" }), TestModelFactory.CreateNativeOptions());

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
    public async Task InferenceWorker_LogsCapabilityDiagnostics_AfterWarmup()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel("model.gguf");
        var logger = new ListLogger<QueuedInferenceWorker>();

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "Hello" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("warmed", 5, 6, 11));

        var worker = new QueuedInferenceWorker(
            new InferenceWorkQueue(Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" })), hostedModel, Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }),
            logger, Mock.Of<IHostApplicationLifetime>(), provider.Object, hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "public-model" }), TestModelFactory.CreateNativeOptions());

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
    public async Task InferenceWorker_LogsSanitizedFallbackModelId_WhenConfiguredModelIdMissing()
    {
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        var model = CreateModel("/tmp/models/fallback.gguf");
        var logger = new ListLogger<QueuedInferenceWorker>();

        provider.Setup(p => p.LoadModelAsync("/tmp/models/fallback.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r => r.Prompt == "Hello" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("warmed", 5, 6, 11));

        var worker = new QueuedInferenceWorker(
            new InferenceWorkQueue(Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" })), hostedModel, Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }),
            logger, Mock.Of<IHostApplicationLifetime>(), provider.Object, hostedModel,
            Options.Create(new HostedModelOptions { ModelPath = "/tmp/models/fallback.gguf", ModelId = string.Empty }), TestModelFactory.CreateNativeOptions());

        await worker.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Messages, message => message.Contains("Loaded model metadata for model fallback", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("Runtime capabilities for model fallback", StringComparison.Ordinal));

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PreparedConstraint_ReachesProviderAsTheSameObject()
    {
        var provider = new Mock<ILlamaProvider>();
        var model = CreateModel();
        var hosted = CreateHostedModel();
        hosted.SetLoaded(model);
        var constraint = JsonStructuredOutput.Parse(null);
        var prepared = new PreparedGenerationRequest("prepared", new InferenceGenerationOptions(8, 0, 1), constraint);
        provider.Setup(p => p.InferAsync(model, prepared, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("{\"ok\":true}", 2, 4, 6));
        var (coordinator, worker) = CreateHarness(provider.Object, hosted);
        using var lifetime = worker;
        await worker.StartAsync(CancellationToken.None);
        await coordinator.InferAsync(prepared, CancellationToken.None);
        provider.Verify(p => p.InferAsync(model, It.Is<PreparedGenerationRequest>(r =>
            ReferenceEquals(r, prepared) && ReferenceEquals(r.Constraint, constraint)), It.IsAny<CancellationToken>()), Times.Once);
        await worker.StopAsync(CancellationToken.None);
    }

    private static (QueuedInferenceCoordinator Coordinator, QueuedInferenceWorker Worker) CreateHarness(
        ILlamaProvider provider,
        IHostedModel hostedModel,
        InferenceOptions? options = null,
        ILogger<QueuedInferenceWorker>? logger = null,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        options ??= new InferenceOptions
        {
            ChannelCapacity = 1,
            WorkerCount = 1,
            AcquireTimeout = TimeSpan.FromSeconds(1),
            StartupWarmupPrompt = "Hello"
        };

        var startupModel = (hostedModel as HostedModel)?.GetSnapshot().Model ?? CreateModel();
        Mock.Get(provider).Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(startupModel);
        Mock.Get(provider).Setup(p => p.InferAsync(startupModel, It.Is<PreparedGenerationRequest>(r => r.Prompt == options.StartupWarmupPrompt && r.Constraint == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InferenceResult("warmed", 1, 1, 2));
        var runtimeInfo = hostedModel as IHostedRuntimeInfo ?? CreateHostedModel();
        var queue = new InferenceWorkQueue(Options.Create(options));
        var coordinator = new QueuedInferenceCoordinator(provider, queue);
        var worker = new QueuedInferenceWorker(
            queue,
            hostedModel,
            Options.Create(options),
            logger ?? NullLogger<QueuedInferenceWorker>.Instance,
            applicationLifetime ?? Mock.Of<IHostApplicationLifetime>(), provider, runtimeInfo,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf", ModelId = "test-model" }),
            TestModelFactory.CreateNativeOptions());

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
                hostedModel.SetWarmingUp(hostedModel.GetSnapshot().Model!);
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
        Mock.Of<IEngineModel>(model => model.SourcePath == sourcePath &&
            model.Metadata == TestModelFactory.CreateModelMetadata(contextSize));
}

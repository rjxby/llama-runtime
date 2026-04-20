using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.HealthChecks;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class QueuedInferenceCoordinatorTests
{
    [Fact]
    public async Task InferAsync_WhenQueueIsSaturated_ThrowsInferenceQueueRejectedException()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        var store = new HostedModelStore();
        store.SetLoaded(Mock.Of<IEngineModel>(m => m.Id == "model"));

        provider.Setup(p => p.InferAsync(It.IsAny<IEngineModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                return "ok";
            });

        var coordinator = new QueuedInferenceCoordinator(
            provider.Object,
            store,
            Options.Create(new InferenceOptions
            {
                ChannelCapacity = 1,
                WorkerCount = 1,
                AcquireTimeout = TimeSpan.FromMilliseconds(100)
            }),
            NullLogger<QueuedInferenceCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);
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
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ModelLoaderWorker_Warms_Model_Before_Marking_It_Loaded()
    {
        var warmupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWarmupToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        var store = new HostedModelStore();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                warmupStarted.TrySetResult();
                await allowWarmupToFinish.Task;
                return "warmed";
            });
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider.Object,
            store,
            store,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf" }),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        var startTask = worker.StartAsync(CancellationToken.None);
        await warmupStarted.Task;

        var warmingSnapshot = store.GetSnapshot();
        Assert.Equal(HostedModelState.WarmingUp, warmingSnapshot.State);
        Assert.Same(model, warmingSnapshot.Model);

        var check = new ModelReadyHealthCheck(store);
        var warming = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, warming.Status);

        allowWarmupToFinish.TrySetResult();
        await startTask;

        var loadedSnapshot = store.GetSnapshot();
        Assert.Equal(HostedModelState.Loaded, loadedSnapshot.State);
        Assert.Same(model, loadedSnapshot.Model);

        await worker.StopAsync(CancellationToken.None);

        var stoppedSnapshot = store.GetSnapshot();
        Assert.Equal(HostedModelState.NotLoaded, stoppedSnapshot.State);
        Assert.Null(stoppedSnapshot.Model);
        provider.Verify(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModelLoaderWorker_WarmupFailure_Fails_Startup_And_Unloads_Model()
    {
        var provider = new Mock<ILlamaProvider>();
        var store = new HostedModelStore();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InferenceException("warmup failed"));
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider.Object,
            store,
            store,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf" }),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        var ex = await Assert.ThrowsAsync<InferenceException>(() => worker.StartAsync(CancellationToken.None));

        Assert.Equal("warmup failed", ex.Message);
        Assert.Equal(HostedModelState.Failed, store.GetSnapshot().State);
        provider.Verify(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModelLoaderWorker_BlankWarmupOutput_Fails_Startup_And_Unloads_Model()
    {
        var provider = new Mock<ILlamaProvider>();
        var store = new HostedModelStore();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.InferAsync(model, "Hello", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmptyInferenceOutputException("Inference returned blank output for a text-generation request."));
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider.Object,
            store,
            store,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf" }),
            Options.Create(new InferenceOptions { StartupWarmupPrompt = "Hello" }));

        var ex = await Assert.ThrowsAsync<EmptyInferenceOutputException>(() => worker.StartAsync(CancellationToken.None));

        Assert.Equal("Inference returned blank output for a text-generation request.", ex.Message);
        Assert.Equal(HostedModelState.Failed, store.GetSnapshot().State);
        provider.Verify(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModelReadyHealthCheck_ReportsLoadingWarmingUpAndFailedStates()
    {
        var store = new HostedModelStore();
        var check = new ModelReadyHealthCheck(store);

        store.SetLoading();
        var loading = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, loading.Status);

        store.SetWarmingUp(Mock.Of<IEngineModel>(m => m.Id == "model"));
        var warmingUp = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, warmingUp.Status);

        store.SetFailed(new InvalidOperationException("boom"));
        var failed = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, failed.Status);
        Assert.Contains("boom", failed.Description);
    }
}

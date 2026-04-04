using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.HealthChecks;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class QueuedInferenceExecutorTests
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

        var executor = new QueuedInferenceExecutor(
            provider.Object,
            store,
            Options.Create(new InferenceOptions
            {
                ChannelCapacity = 1,
                WorkerCount = 1,
                AcquireTimeout = TimeSpan.FromMilliseconds(100)
            }),
            NullLogger<QueuedInferenceExecutor>.Instance);

        await executor.StartAsync(CancellationToken.None);
        try
        {
            var inFlight = executor.InferAsync("first", CancellationToken.None);
            var queued = executor.InferAsync("second", CancellationToken.None);

            var ex = await Assert.ThrowsAsync<InferenceQueueRejectedException>(() =>
                executor.InferAsync("third", CancellationToken.None));

            Assert.Contains("did not accept", ex.Message);

            gate.TrySetResult();
            await Task.WhenAll(inFlight, queued);
        }
        finally
        {
            gate.TrySetResult();
            await executor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ModelLoaderWorker_StartAndStop_ManageHostedModelLifecycle()
    {
        var provider = new Mock<ILlamaProvider>();
        var store = new HostedModelStore();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");

        provider.Setup(p => p.LoadModelAsync("model.gguf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        provider.Setup(p => p.UnloadModelAsync(model, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var worker = new ModelLoaderWorker(
            NullLogger<ModelLoaderWorker>.Instance,
            provider.Object,
            store,
            Options.Create(new HostedModelOptions { ModelPath = "model.gguf" }),
            Options.Create(new InferenceOptions { EnableStartupWarmup = false }));

        await worker.StartAsync(CancellationToken.None);

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
    public async Task ModelReadyHealthCheck_ReportsLoadingAndFailedStates()
    {
        var store = new HostedModelStore();
        var check = new ModelReadyHealthCheck(store);

        store.SetLoading();
        var loading = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, loading.Status);

        store.SetFailed(new InvalidOperationException("boom"));
        var failed = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, failed.Status);
        Assert.Contains("boom", failed.Description);
    }
}

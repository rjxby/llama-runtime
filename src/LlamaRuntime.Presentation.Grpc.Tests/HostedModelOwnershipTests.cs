using System.Collections.Concurrent;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.ModelHosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class HostedModelOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiDisposal_ThroughAllAliases_InitiatesCleanupWithoutWaiting(bool asynchronous)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Mock<IEngineModel>();
        model.Setup(m => m.DisposeAsync()).Returns(() => new ValueTask(release.Task));
        var services = new ServiceCollection();
        services.AddHostedRuntime();
        services.AddSingleton(TestModelFactory.CreateNativeOptions());
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<HostedModel>();
        Assert.Same(hosted, provider.GetRequiredService<IHostedModel>());
        Assert.Same(hosted, provider.GetRequiredService<IHostedRuntimeInfo>());
        hosted.SetWarmingUp(model.Object);
        hosted.SetLoaded(model.Object);

        try
        {
            await Task.Run(async () =>
            {
                if (asynchronous)
                {
                    await provider.DisposeAsync();
                }
                else
                {
                    provider.Dispose();
                }
            }).WaitAsync(TimeSpan.FromSeconds(2));

            var cleanup = hosted.CloseAsync();
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(HostedModelState.Stopping, hosted.GetSnapshot().State);
            Assert.False(hosted.TryGetLoadedModel(out var unavailable));
            Assert.Null(unavailable);
            model.Verify(m => m.DisposeAsync(), Times.Once);
            release.SetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
            await hosted.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentDisposal_SharesOneCleanupTask()
    {
        using var hosted = new HostedModel(TestModelFactory.CreateNativeOptions());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Mock<IEngineModel>();
        model.Setup(m => m.DisposeAsync()).Returns(() => new ValueTask(release.Task));
        hosted.SetLoaded(model.Object);
        var cleanups = new ConcurrentBag<Task>();

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            {
                hosted.Dispose();
                cleanups.Add(hosted.CloseAsync());
            }))).WaitAsync(TimeSpan.FromSeconds(2));

            var cleanup = hosted.CloseAsync();
            Assert.False(cleanup.IsCompleted);
            Assert.All(cleanups, task => Assert.Same(cleanup, task));
            model.Verify(m => m.DisposeAsync(), Times.Once);
        }
        finally
        {
            release.TrySetResult();
            await hosted.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(HostedModelState.Loading)]
    [InlineData(HostedModelState.Failed)]
    [InlineData(HostedModelState.NotLoaded)]
    public async Task ClearingSnapshot_RetainsOwnership(HostedModelState state)
    {
        using var hosted = new HostedModel(TestModelFactory.CreateNativeOptions());
        var model = new Mock<IEngineModel>();
        model.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);
        hosted.SetLoaded(model.Object, "test-model");
        switch (state)
        {
            case HostedModelState.Loading:
                hosted.SetLoading();
                break;
            case HostedModelState.Failed:
                hosted.SetFailed(new InvalidOperationException("startup failed"));
                break;
            case HostedModelState.NotLoaded:
                hosted.Reset();
                break;
        }

        Assert.Null(hosted.GetSnapshot().Model);
        Assert.Throws<InvalidOperationException>(() => hosted.SetLoaded(Mock.Of<IEngineModel>()));
        model.Verify(m => m.DisposeAsync(), Times.Never);
        await hosted.CloseAsync();
        model.Verify(m => m.DisposeAsync(), Times.Once);
        if (state == HostedModelState.Failed)
        {
            var snapshot = hosted.GetSnapshot();
            Assert.Equal(HostedModelState.Failed, snapshot.State);
            Assert.Equal("startup failed", snapshot.FailureMessage);
            Assert.Equal("test-model", snapshot.ConfiguredModelId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_RejectedWithoutTakingOwnership(bool warmingUp)
    {
        using var hosted = new HostedModel(TestModelFactory.CreateNativeOptions());
        var original = new Mock<IEngineModel>();
        original.Setup(m => m.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var replacement = new Mock<IEngineModel>();
        hosted.SetWarmingUp(original.Object);
        hosted.SetWarmingUp(original.Object);
        hosted.SetLoaded(original.Object);
        hosted.SetLoaded(original.Object);

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (warmingUp)
            {
                hosted.SetWarmingUp(replacement.Object);
            }
            else
            {
                hosted.SetLoaded(replacement.Object);
            }
        });

        Assert.True(hosted.TryGetLoadedModel(out var loaded));
        Assert.Same(original.Object, loaded);
        await hosted.CloseAsync();
        original.Verify(m => m.DisposeAsync(), Times.Once);
        replacement.Verify(m => m.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task ClosedOwner_AllowsReportingButCannotReopen()
    {
        using var hosted = new HostedModel(TestModelFactory.CreateNativeOptions());
        var model = new Mock<IEngineModel>();
        await hosted.CloseAsync();
        hosted.SetFailed(new InvalidOperationException("load finished after shutdown"));
        Assert.Equal(HostedModelState.Failed, hosted.GetSnapshot().State);
        hosted.SetStopping();
        hosted.Reset();
        Assert.Equal(HostedModelState.NotLoaded, hosted.GetSnapshot().State);
        Assert.Throws<ObjectDisposedException>(() => hosted.SetLoading());
        Assert.Throws<ObjectDisposedException>(() => hosted.SetWarmingUp(model.Object));
        Assert.Throws<ObjectDisposedException>(() => hosted.SetLoaded(model.Object));
        Assert.False(hosted.TryGetLoadedModel(out var loaded));
        Assert.Null(loaded);
        model.Verify(m => m.DisposeAsync(), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailure_RemainsOnSharedTask(bool synchronous)
    {
        using var hosted = new HostedModel(TestModelFactory.CreateNativeOptions());
        var failure = new IOException("cleanup failed");
        var model = new Mock<IEngineModel>();
        model.Setup(m => m.DisposeAsync()).Returns(() =>
        {
            if (synchronous)
            {
                throw failure;
            }

            return new ValueTask(Task.FromException(failure));
        });
        hosted.SetLoaded(model.Object);
        hosted.Dispose();
        var cleanup = hosted.CloseAsync();

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => cleanup));
        hosted.Dispose();
        Assert.Same(cleanup, hosted.CloseAsync());
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => hosted.CloseAsync()));
        model.Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Cleanup_CanReadSnapshotFromAnotherThread()
    {
        using var hosted = new HostedModel(TestModelFactory.CreateNativeOptions());
        var model = new Mock<IEngineModel>();
        model.Setup(m => m.DisposeAsync()).Returns(() =>
        {
            var snapshot = Task.Run(hosted.GetSnapshot).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Assert.Equal(HostedModelState.Stopping, snapshot.State);
            return ValueTask.CompletedTask;
        });
        hosted.SetLoaded(model.Object);

        await hosted.CloseAsync();
    }
}

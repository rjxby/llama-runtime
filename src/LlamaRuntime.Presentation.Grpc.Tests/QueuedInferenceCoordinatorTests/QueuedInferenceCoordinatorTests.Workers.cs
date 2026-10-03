using System.Collections.Concurrent;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.ModelHosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace LlamaRuntime.Presentation.Grpc.Tests;

public sealed partial class QueuedInferenceCoordinatorTests
{
    [Fact]
    public async Task Workers_WithPrequeuedBlockingRequests_RunIndependentlyWithinCapacity()
    {
        using var release = new ManualResetEventSlim();
        using var callers = new CancellationTokenSource();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCounts = new ConcurrentBag<int>();
        var active = 0;
        var entered = 0;
        var provider = new Mock<ILlamaProvider>();
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel());
        provider.Setup(p => p.InferAsync(It.IsAny<IEngineModel>(), It.Is<PreparedGenerationRequest>(r => r.Constraint == null), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                activeCounts.Add(Interlocked.Increment(ref active));
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothStarted.TrySetResult();
                }

                try
                {
                    if (!release.Wait(TimeSpan.FromSeconds(15)))
                    {
                        throw new TimeoutException("Blocking inference was not released.");
                    }

                    return Task.FromResult(new InferenceResult("ok", 5, 2, 7));
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        var (coordinator, worker) = CreateHarness(provider.Object, hostedModel, new InferenceOptions
        {
            WorkerCount = 2,
            ChannelCapacity = 2,
            AcquireTimeout = TimeSpan.FromMilliseconds(100)
        });
        using var workerLifetime = worker;
        var first = coordinator.InferAsync(new PreparedGenerationRequest("first", new InferenceGenerationOptions(8, 0, 1)), callers.Token, null);
        var second = coordinator.InferAsync(new PreparedGenerationRequest("second", new InferenceGenerationOptions(8, 0, 1)), callers.Token, null);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var third = coordinator.InferAsync(new PreparedGenerationRequest("third", new InferenceGenerationOptions(8, 0, 1)), callers.Token, null);
            var fourth = coordinator.InferAsync(new PreparedGenerationRequest("fourth", new InferenceGenerationOptions(8, 0, 1)), callers.Token, null);
            await Assert.ThrowsAsync<InferenceQueueRejectedException>(() =>
                coordinator.InferAsync(new PreparedGenerationRequest("overflow", new InferenceGenerationOptions(8, 0, 1)), callers.Token, null).WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(2, Volatile.Read(ref entered));
            Assert.False(third.IsCompleted);
            Assert.False(fourth.IsCompleted);

            release.Set();
            var results = await Task.WhenAll(first, second, third, fourth).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(results, result => Assert.Equal("ok", result.Content));
            Assert.Equal(2, activeCounts.Max());
            Assert.Equal(4, Volatile.Read(ref entered));
        }
        finally
        {
            release.Set();
            await callers.CancelAsync();
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_UnexpectedFailure_RequestsHostStopBeforeBlockedSiblingFinishes(bool loggerThrows)
    {
        using var release = new ManualResetEventSlim();
        using var failedCaller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Hosted model lookup failed.");
        IEngineModel? model = CreateModel();
        var hostedModel = new Mock<IHostedModel>();
        hostedModel.Setup(m => m.CloseAsync()).Returns(Task.CompletedTask);
        hostedModel.SetupSequence(m => m.TryGetLoadedModel(out model))
            .Returns(true)
            .Throws(failure);
        var applicationLifetime = new Mock<IHostApplicationLifetime>();
        applicationLifetime.Setup(lifetime => lifetime.StopApplication())
            .Callback(() => stopRequested.TrySetResult());
        var logger = new Mock<ILogger<QueuedInferenceWorker>>();
        var loggingAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggingFailure = new InvalidOperationException("Logging failed.");
        var errorLog = logger.Setup(log => log.Log(
                LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() =>
            {
                Assert.True(stopRequested.Task.IsCompletedSuccessfully);
                loggingAttempted.TrySetResult();
            });
        if (loggerThrows)
        {
            errorLog.Throws(loggingFailure);
        }
        var provider = new Mock<ILlamaProvider>();
        provider.Setup(p => p.InferAsync(It.IsAny<IEngineModel>(), It.Is<PreparedGenerationRequest>(r => r.Prompt == "blocking" && r.Constraint == null), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("Blocking inference was not released.");
                }

                return Task.FromResult(new InferenceResult("ok", 5, 2, 7));
            });
        var (coordinator, worker) = CreateHarness(
            provider.Object, hostedModel.Object,
            new InferenceOptions { WorkerCount = 2 }, logger.Object, applicationLifetime.Object);
        using var workerLifetime = worker;
        var blocking = coordinator.InferAsync(new PreparedGenerationRequest("blocking", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failed = coordinator.InferAsync(new PreparedGenerationRequest("failure", new InferenceGenerationOptions(8, 0, 1)), failedCaller.Token, null);
            await stopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await loggingAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<ModelNotFoundException>(() => blocking.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(worker.ExecuteTask!.IsCompleted);
            applicationLifetime.Verify(lifetime => lifetime.StopApplication(), Times.Once);
            logger.Verify(log => log.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("worker_id=")),
                    failure,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);

            // Shutdown cancellation can finish the caller before its failure continuation runs.
            var callerFailure = await Record.ExceptionAsync(() => failed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(ReferenceEquals(failure, callerFailure) || callerFailure is ModelNotFoundException);
        }
        finally
        {
            release.Set();
            await failedCaller.CancelAsync();
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Assert.ThrowsAsync<ModelNotFoundException>(() => blocking.WaitAsync(TimeSpan.FromSeconds(5)));
        if (loggerThrows)
        {
            var reported = await Assert.ThrowsAsync<AggregateException>(() =>
                worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { failure, loggingFailure }, reported.InnerExceptions);
        }
        else
        {
            var reported = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, reported);
        }
    }

    [Fact]
    public async Task Worker_UnexpectedProviderFailure_CompletesCallerAndStopsHost()
    {
        var failure = new InvalidOperationException("Unexpected provider failure.");
        var provider = new Mock<ILlamaProvider>();
        provider.Setup(p => p.InferAsync(It.IsAny<IEngineModel>(),
                It.Is<PreparedGenerationRequest>(r => r.Prompt == "failure"), It.IsAny<CancellationToken>()))
            .Throws(failure);
        var lifetime = new Mock<IHostApplicationLifetime>();
        var (coordinator, worker) = CreateHarness(provider.Object, CreateHostedModel(),
            applicationLifetime: lifetime.Object);
        using var workerLifetime = worker;
        await worker.StartAsync(CancellationToken.None);
        var reported = await Record.ExceptionAsync(() => coordinator.InferAsync(
            new PreparedGenerationRequest("failure", new InferenceGenerationOptions(8, 0, 1)),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(failure, reported) || reported is ModelNotFoundException);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5))));
        lifetime.Verify(l => l.StopApplication(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_ExpectedProviderFailure_CompletesCallerAndProcessesNextRequest(bool nativeCancelled)
    {
        Exception failure = nativeCancelled
            ? new OperationCanceledException("Native execution aborted.")
            : new InferenceException("Inference failed.");
        var provider = new Mock<ILlamaProvider>();
        provider.SetupSequence(p => p.InferAsync(It.IsAny<IEngineModel>(), It.Is<PreparedGenerationRequest>(r => r.Constraint == null), It.IsAny<CancellationToken>()))
            .Throws(failure)
            .ReturnsAsync(new InferenceResult("ok", 5, 2, 7));
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel());
        var applicationLifetime = new Mock<IHostApplicationLifetime>();
        var (coordinator, worker) = CreateHarness(
            provider.Object, hostedModel, applicationLifetime: applicationLifetime.Object);
        using var workerLifetime = worker;

        try
        {
            await worker.StartAsync(CancellationToken.None);
            var reported = await Record.ExceptionAsync(() =>
                coordinator.InferAsync(new PreparedGenerationRequest("failure", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, reported);
            var result = await coordinator.InferAsync(new PreparedGenerationRequest("next", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ok", result.Content);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }

        applicationLifetime.Verify(lifetime => lifetime.StopApplication(), Times.Never);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Worker_ExpectedCancellation_DoesNotReportWorkerFailure(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlamaProvider>();
        provider.Setup(p => p.InferAsync(It.IsAny<IEngineModel>(), It.Is<PreparedGenerationRequest>(r => r.Constraint == null), It.IsAny<CancellationToken>()))
            .Returns(async (IEngineModel _, PreparedGenerationRequest _, CancellationToken ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new InferenceResult("unreachable", 0, 0, 0);
            });
        var hostedModel = CreateHostedModel();
        hostedModel.SetLoaded(CreateModel());
        var applicationLifetime = new Mock<IHostApplicationLifetime>();
        var (coordinator, worker) = CreateHarness(
            provider.Object, hostedModel, applicationLifetime: applicationLifetime.Object);
        using var workerLifetime = worker;

        try
        {
            await worker.StartAsync(CancellationToken.None);
            var request = coordinator.InferAsync(new PreparedGenerationRequest("cancel", new InferenceGenerationOptions(8, 0, 1)), caller.Token, null);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelCaller)
            {
                await caller.CancelAsync();
            }
            else
            {
                await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (cancelCaller)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                await Assert.ThrowsAsync<ModelNotFoundException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        finally
        {
            await caller.CancelAsync();
            await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }

        applicationLifetime.Verify(lifetime => lifetime.StopApplication(), Times.Never);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }
}

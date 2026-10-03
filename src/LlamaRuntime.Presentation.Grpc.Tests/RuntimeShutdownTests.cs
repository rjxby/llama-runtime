using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.Inference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class RuntimeShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutStopAndServiceProviderDisposalRetainBlockedNativeResources(bool asynchronous)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var handle = TestModelFactory.CreateModelHandle();
        var context = TestModelFactory.CreateContextHandle(10);
        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.LoadModel("model.gguf")).Returns(handle);
        native.Setup(n => n.GetModelMetadata(handle)).Returns(new NativeModelMetadata(32, NativeTokenizerType.SentencePiece));
        native.Setup(n => n.CreateContext(handle)).Returns(context);
        native.Setup(n => n.GetContextMetadata(context)).Returns(new NativeContextMetadata(32));
        native.Setup(n => n.Infer(context, It.IsAny<string>(), NativeInferenceResponseFormat.Text,
                null, It.IsAny<NativeGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<LlamaContextHandle, string, NativeInferenceResponseFormat, string?, NativeGenerationOptions?, CancellationToken>(
                (_, prompt, _, _, _, _) => {
                    if (prompt == "blocked")
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException();
                        }

                        Assert.False(handle.IsClosed);
                        Assert.False(context.IsClosed);
                    }
                    return new NativeInferenceResult("ok", 2, 1, 3);
                });
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["HostedModel:ModelPath"] = "model.gguf", ["HostedModel:ModelId"] = "test-model",
            ["Llama:Native:NativeLibraryPath"] = "test-native", ["Llama:Native:ContextSize"] = "32",
            ["Llama:Native:BatchSize"] = "8", ["Llama:Native:GenerationMaxNewTokens"] = "8",
            ["Inference:WorkerCount"] = "1", ["Inference:ChannelCapacity"] = "4"
        });
        builder.Services.AddHostedRuntime();
        builder.Services.AddSingleton(native.Object);
        var host = builder.Build();
        await host.StartAsync();
        var worker = Assert.IsType<QueuedInferenceWorker>(host.Services.GetServices<IHostedService>().Single());
        var coordinator = host.Services.GetRequiredService<IInferenceCoordinator>();
        var active = coordinator.InferAsync(new PreparedGenerationRequest("blocked", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var queued = coordinator.InferAsync(new PreparedGenerationRequest("queued", new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None, null);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await host.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }
            await Assert.ThrowsAsync<ModelNotFoundException>(() => active.WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<ModelNotFoundException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
            await Task.Run(async () =>
            {
                if (asynchronous)
                {
                    await ((IAsyncDisposable)host).DisposeAsync();
                }
                else
                {
                    host.Dispose();
                }
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(worker.ExecuteTask!.IsCompleted);
            Assert.False(handle.IsClosed);
            Assert.False(context.IsClosed);
            release.Set();
            await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(context.IsClosed);
            Assert.True(handle.IsClosed);
            native.Verify(n => n.Infer(context, "queued", It.IsAny<NativeInferenceResponseFormat>(),
                It.IsAny<string?>(), It.IsAny<NativeGenerationOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            release.Set();
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
            host.Dispose();
        }
    }
    [Fact]
    public async Task ThrowingWorkerLogger_DoesNotReleaseBlockedNativeResourcesOrStrandCallers()
    {
        using var entered = new ManualResetEventSlim();
        using var failureEntered = new ManualResetEventSlim();
        using var fail = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var handle = TestModelFactory.CreateModelHandle();
        var blockedContext = TestModelFactory.CreateContextHandle(10);
        var failedContext = TestModelFactory.CreateContextHandle(11);
        var failure = new InvalidOperationException("Native invocation failed unexpectedly.");
        var loggingFailure = new IOException("Worker error logging failed.");
        var logger = new Mock<ILogger<QueuedInferenceWorker>>();
        logger.Setup(log => log.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Throws(loggingFailure);
        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.LoadModel("model.gguf")).Returns(handle);
        native.Setup(n => n.GetModelMetadata(handle)).Returns(new NativeModelMetadata(32, NativeTokenizerType.SentencePiece));
        native.SetupSequence(n => n.CreateContext(handle)).Returns(blockedContext).Returns(failedContext);
        native.Setup(n => n.GetContextMetadata(It.IsAny<LlamaContextHandle>())).Returns(new NativeContextMetadata(32));
        native.Setup(n => n.Infer(It.IsAny<LlamaContextHandle>(), It.IsAny<string>(), NativeInferenceResponseFormat.Text,
                null, It.IsAny<NativeGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<LlamaContextHandle, string, NativeInferenceResponseFormat, string?, NativeGenerationOptions?, CancellationToken>(
                (context, prompt, _, _, _, _) =>
                {
                    if (prompt == "blocked")
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException("Blocked inference was not released.");
                        }
                        Assert.False(handle.IsClosed);
                        Assert.False(context.IsClosed);
                    }
                    else if (prompt == "failure")
                    {
                        failureEntered.Set();
                        if (!fail.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException("Failure was not released.");
                        }
                        throw failure;
                    }
                    return new NativeInferenceResult("ok", 2, 1, 3);
                });
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostedModel:ModelPath"] = "model.gguf", ["HostedModel:ModelId"] = "test-model",
            ["Llama:Native:NativeLibraryPath"] = "test-native", ["Llama:Native:ContextSize"] = "32",
            ["Llama:Native:BatchSize"] = "8", ["Llama:Native:GenerationMaxNewTokens"] = "8",
            ["Inference:WorkerCount"] = "2", ["Inference:ChannelCapacity"] = "4"
        });
        builder.Services.AddHostedRuntime();
        builder.Services.AddSingleton(native.Object);
        builder.Services.AddSingleton(logger.Object);
        var host = builder.Build();
        await host.StartAsync();
        var worker = Assert.IsType<QueuedInferenceWorker>(host.Services.GetServices<IHostedService>().Single());
        var coordinator = host.Services.GetRequiredService<IInferenceCoordinator>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
        Task<InferenceResult> Infer(string prompt) => coordinator.InferAsync(
            new PreparedGenerationRequest(prompt, new InferenceGenerationOptions(8, 0, 1)), CancellationToken.None);
        try
        {
            var active = Infer("blocked");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var failed = Infer("failure");
            Assert.True(failureEntered.Wait(TimeSpan.FromSeconds(2)));
            var queued = Infer("queued");
            fail.Set();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var callerFailure = await Record.ExceptionAsync(() => failed.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(ReferenceEquals(failure, callerFailure) || callerFailure is ModelNotFoundException);
            await Assert.ThrowsAsync<ModelNotFoundException>(() => active.WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<ModelNotFoundException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await host.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }
            host.Dispose();
            Assert.False(worker.ExecuteTask!.IsCompleted);
            Assert.False(handle.IsClosed);
            Assert.False(blockedContext.IsClosed);
            release.Set();
            var reported = await Assert.ThrowsAsync<AggregateException>(() => worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(new Exception[] { failure, loggingFailure }, reported.InnerExceptions);
            Assert.True(handle.IsClosed);
            Assert.True(blockedContext.IsClosed);
            Assert.True(failedContext.IsClosed);
            native.Verify(n => n.Infer(It.IsAny<LlamaContextHandle>(), "queued", It.IsAny<NativeInferenceResponseFormat>(),
                It.IsAny<string?>(), It.IsAny<NativeGenerationOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            fail.Set();
            release.Set();
            await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2)));
            host.Dispose();
        }
    }

}

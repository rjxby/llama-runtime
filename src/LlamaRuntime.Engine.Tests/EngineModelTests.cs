using LlamaRuntime.Common.Tests;
using LlamaRuntime.Native.Contracts;
using Moq;

namespace LlamaRuntime.Engine.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class EngineModelTests
{
    private static EngineModel Model(Mock<ILlamaNative> native, LlamaModelHandle handle,
        int capacity = 1, LlamaContextHandle? initial = null) =>
        new("test.gguf", handle, TestModelFactory.CreateModelMetadata(), native.Object,
            capacity, initial);

    [Fact]
    public async Task Leases_AreExclusiveBoundedAndReuseStartupContext()
    {
        var native = new Mock<ILlamaNative>();
        var handle = TestModelFactory.CreateModelHandle();
        var first = TestModelFactory.CreateContextHandle(10);
        var second = TestModelFactory.CreateContextHandle(11);
        native.Setup(n => n.CreateContext(handle)).Returns(second);
        await using var model = Model(native, handle, 2, first);
        var a = await model.CreateSessionAsync();
        var b = await model.CreateSessionAsync();
        var waiting = model.CreateSessionAsync();
        Assert.False(waiting.IsCompleted);
        await a.DisposeAsync();
        var c = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await c.CountTokensAsync("reused");
        native.Verify(n => n.CountTokens(first, "reused"), Times.Once);
        native.Verify(n => n.CreateContext(handle), Times.Once);
        await b.DisposeAsync();
        await c.DisposeAsync();
    }

    [Fact]
    public async Task Close_WakesWaitersAndRetainsActiveResourcesUntilRelease()
    {
        var native = new Mock<ILlamaNative>();
        var handle = TestModelFactory.CreateModelHandle();
        var context = TestModelFactory.CreateContextHandle(10);
        var model = Model(native, handle, initial: context);
        var active = await model.CreateSessionAsync();
        var waiting = model.CreateSessionAsync();
        model.Dispose();
        var closing = model.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.False(handle.IsClosed);
        Assert.False(context.IsClosed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => model.CreateSessionAsync());
        await active.DisposeAsync();
        await active.DisposeAsync();
        await closing.WaitAsync(TimeSpan.FromSeconds(2));
        await model.DisposeAsync();
        Assert.True(context.IsClosed);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public async Task Close_DuringNativeContextCreationWaitsForCreationAndDestruction()
    {
        var native = new Mock<ILlamaNative>();
        var handle = TestModelFactory.CreateModelHandle();
        var context = TestModelFactory.CreateContextHandle(10);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        native.Setup(n => n.CreateContext(handle)).Returns(() => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException();
            }

            Assert.False(handle.IsClosed);
            return context;
        });
        var model = Model(native, handle);
        var acquiring = Task.Run(() => model.CreateSessionAsync());
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var closing = model.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.False(handle.IsClosed);
            release.Set();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => acquiring);
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(context.IsClosed);
            Assert.True(handle.IsClosed);
        }
        finally
        {
            release.Set();
            await model.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposingSessionDuringInferenceCannotReturnItsContextEarly()
    {
        var native = new Mock<ILlamaNative>();
        var handle = TestModelFactory.CreateModelHandle();
        var context = TestModelFactory.CreateContextHandle(10);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        native.Setup(n => n.Infer(context, "blocked", NativeInferenceResponseFormat.Text,
                null, It.IsAny<NativeGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(() => {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException();
                }

                Assert.False(context.IsClosed);
                Assert.False(handle.IsClosed);
                return new NativeInferenceResult("ok", 1, 1, 2);
            });
        var model = Model(native, handle, initial: context);
        var session = await model.CreateSessionAsync();
        var inference = Task.Run(() => session.InferAsync("blocked"));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var disposal = Task.Run(async () => await session.DisposeAsync());
            var closing = model.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.False(context.IsClosed);
            release.Set();
            await inference;
            await disposal;
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(context.IsClosed);
            Assert.True(handle.IsClosed);
        }
        finally
        {
            release.Set();
            await session.DisposeAsync();
            await model.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelledAcquisitionDoesNotLoseCapacity()
    {
        var native = new Mock<ILlamaNative>();
        var handle = TestModelFactory.CreateModelHandle();
        var context = TestModelFactory.CreateContextHandle(10);
        await using var model = Model(native, handle, initial: context);
        var first = await model.CreateSessionAsync();
        using var cts = new CancellationTokenSource();
        var waiting = model.CreateSessionAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await first.DisposeAsync();
        await using var reused = await model.CreateSessionAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FailedCreationDoesNotLoseCapacity()
    {
        var native = new Mock<ILlamaNative>();
        var handle = TestModelFactory.CreateModelHandle();
        native.SetupSequence(n => n.CreateContext(handle))
            .Throws(new NativeOutOfMemoryException("allocation failed"))
            .Returns(TestModelFactory.CreateContextHandle(10));
        await using var model = Model(native, handle);
        await Assert.ThrowsAsync<NativeOutOfMemoryException>(() => model.CreateSessionAsync());
        await using var session = await model.CreateSessionAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
}

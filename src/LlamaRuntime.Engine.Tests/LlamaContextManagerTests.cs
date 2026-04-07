using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Common.Tests;

namespace LlamaRuntime.Engine.Tests;


[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class LlamaContextManagerTests
{
    private static LlamaModelHandle CreateModelHandle() =>
        LlamaModelHandle.FromIntPtr(new IntPtr(1));

    private static LlamaContextHandle CreateContextHandle(int id) =>
        LlamaContextHandle.FromIntPtr(new IntPtr(id));

    private static LlamaContextManager CreateManager(
        Mock<ILlamaNative> nativeMock,
        int poolSize = 2)
    {
        var options = Options.Create(new LlamaProviderOptions
        {
            DefaultPoolSize = poolSize
        });
        return new LlamaContextManager(nativeMock.Object, options, NullLogger<LlamaContextManager>.Instance);
    }

    [Fact]
    public async Task CreateSessionAsync_Context_Is_Reused_After_Session_Disposal()
    {
        var modelHandle = CreateModelHandle();
        var ctxHandle = CreateContextHandle(1);

        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.CreateContext(modelHandle))
              .Returns(ctxHandle);

        var manager = CreateManager(native, poolSize: 1);
        var model = new EngineModel("test", modelHandle);

        await using (var first = await manager.CreateSessionAsync(model))
        {
            await first.CountTokensAsync("a");
        }

        await using (var second = await manager.CreateSessionAsync(model))
        {
            await second.CountTokensAsync("b");
        }

        native.Verify(n => n.CreateContext(modelHandle), Times.Once);
        native.Verify(n => n.ResetContext(ctxHandle), Times.Once);
    }

    [Fact]
    public async Task CreateSessionAsync_Concurrent_Requests_Do_Not_Share_Context()
    {
        var modelHandle = CreateModelHandle();
        var contexts = new Queue<LlamaContextHandle>(new[]
        {
            CreateContextHandle(1),
            CreateContextHandle(2)
        });

        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.CreateContext(modelHandle))
              .Returns(() => contexts.Dequeue());

        var manager = CreateManager(native, poolSize: 2);
        var model = new EngineModel("test", modelHandle);

        var sessions = await Task.WhenAll(
            Enumerable.Range(0, 2)
                .Select(_ => manager.CreateSessionAsync(model)));

        var tasks = sessions
            .Select(async session =>
            {
                await using (session.ConfigureAwait(false))
                {
                    await Task.Delay(50);
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        native.Verify(n => n.CreateContext(modelHandle), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateSessionAsync_When_Model_Not_EngineModel_Throws()
    {
        var native = new Mock<ILlamaNative>();
        var manager = CreateManager(native);

        var fakeModel = Mock.Of<IEngineModel>(m => m.Id == "x");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.CreateSessionAsync(fakeModel));
    }
}

using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class ConcurrencyWiringTests
{
    [Fact]
    public async Task InferenceWorkerCount_Drives_ContextPoolConcurrency()
    {
        var modelHandle = LlamaModelHandle.FromIntPtr(new IntPtr(1));
        var contexts = new Queue<LlamaContextHandle>(new[]
        {
            LlamaContextHandle.FromIntPtr(new IntPtr(11)),
            LlamaContextHandle.FromIntPtr(new IntPtr(12))
        });

        var native = new Mock<ILlamaNative>();
        native.Setup(n => n.CreateContext(modelHandle))
            .Returns(() => contexts.Dequeue());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<InferenceOptions>(options => options.WorkerCount = 2);
        services.AddSingleton(native.Object);
        services.AddLlamaProvider();

        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ILlamaContextManager>();
        var model = new EngineModel("test", modelHandle);

        var sessions = await Task.WhenAll(
            Enumerable.Range(0, 2)
                .Select(_ => manager.CreateSessionAsync(model, CancellationToken.None)));

        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }

        native.Verify(n => n.CreateContext(modelHandle), Times.Exactly(2));
    }
}

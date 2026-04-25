using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.Inference;
using LlamaRuntime.Presentation.Grpc.ModelHosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
        var model = new EngineModel("test.gguf", modelHandle, TestModelFactory.CreateModelMetadata());

        var sessions = await Task.WhenAll(
            Enumerable.Range(0, 2)
                .Select(_ => manager.CreateSessionAsync(model, CancellationToken.None)));

        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }

        native.Verify(n => n.CreateContext(modelHandle), Times.Exactly(2));
    }

    [Fact]
    public async Task AddHostedRuntime_Composes_RuntimeServices_ForStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(CreateRuntimeConfiguration());
        builder.Services.AddHostedRuntime();
        builder.Services.AddSingleton(Mock.Of<ILlamaNative>());
        builder.Services.AddSingleton(Mock.Of<ILlamaProvider>());

        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<IHostedModel>());
        Assert.NotNull(host.Services.GetRequiredService<IHostedRuntimeInfo>());
        Assert.NotNull(host.Services.GetRequiredService<IInferenceCoordinator>());
        await host.StopAsync();
    }

    [Fact]
    public async Task AddHostedRuntime_InvalidHostedModelOptions_FailOnStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostedModel:ModelId"] = "test-model",
            ["Llama:Native:NativeLibraryPath"] = "test-native",
            ["Llama:Native:ContextSize"] = "32",
            ["Llama:Native:BatchSize"] = "8",
            ["Llama:Native:GenerationMaxNewTokens"] = "8",
            ["Inference:ChannelCapacity"] = "4",
            ["Inference:WorkerCount"] = "1",
            ["Inference:AcquireTimeout"] = "00:00:01",
            ["Inference:StartupWarmupPrompt"] = "Hello"
        });
        builder.Services.AddHostedRuntime();

        using var host = builder.Build();
        var validator = host.Services.GetRequiredService<IStartupValidator>();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => Task.Run(validator.Validate));
        Assert.Contains(nameof(HostedModelOptions.ModelPath), string.Join(Environment.NewLine, ex.Failures));
    }

    private static IConfiguration CreateRuntimeConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostedModel:ModelPath"] = "test-model.gguf",
                ["HostedModel:ModelId"] = "test-model",
                ["Llama:Native:NativeLibraryPath"] = "test-native",
                ["Llama:Native:ContextSize"] = "32",
                ["Llama:Native:BatchSize"] = "8",
                ["Llama:Native:GenerationMaxNewTokens"] = "8",
                ["Llama:Native:InferenceBufferSize"] = "1024",
                ["Inference:ChannelCapacity"] = "4",
                ["Inference:WorkerCount"] = "1",
                ["Inference:AcquireTimeout"] = "00:00:01",
                ["Inference:StartupWarmupPrompt"] = "Hello"
            })
            .Build();
}

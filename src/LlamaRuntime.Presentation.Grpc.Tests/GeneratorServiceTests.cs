using Grpc.Core;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.ModelHosting;
using LlamaRuntime.Presentation.Grpc.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class GeneratorServiceTests
{
    [Fact]
    public async Task Generate_BlankInferenceOutput_ReturnsInternalError()
    {
        var provider = new Mock<ILlamaProvider>();
        var store = new HostedModelStore();
        var model = Mock.Of<IEngineModel>(m => m.Id == "model");
        store.SetLoaded(model);

        var coordinator = new QueuedInferenceCoordinator(
            provider.Object,
            store,
            Options.Create(new Configuration.InferenceOptions
            {
                ChannelCapacity = 1,
                WorkerCount = 1,
                AcquireTimeout = TimeSpan.FromSeconds(1)
            }),
            NullLogger<QueuedInferenceCoordinator>.Instance);

        provider.Setup(p => p.InferAsync(model, "world", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmptyInferenceOutputException("Inference returned blank output for a text-generation request."));

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var service = new GeneratorService(
                NullLogger<GeneratorService>.Instance,
                store,
                coordinator,
                Options.Create(new LlamaNativeOptions
                {
                    NativeLibraryPath = "test-native",
                    ContextSize = 32,
                    GenerationMaxNewTokens = 8
                }));

            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                service.Generate(
                    new GenerateRequest
                    {
                        RequestId = "blank-output",
                        Prompt = "world"
                    },
                    TestServerCallContext.Create()));

            Assert.Equal(StatusCode.Internal, ex.StatusCode);
            Assert.Equal("Inference returned blank output for a text-generation request.", ex.Status.Detail);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }
}

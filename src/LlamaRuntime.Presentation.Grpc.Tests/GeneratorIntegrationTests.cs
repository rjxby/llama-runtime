using Microsoft.AspNetCore.Mvc.Testing;
using Grpc.Net.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LlamaRuntime.Presentation.Grpc.Auth;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Presentation.Grpc.Services;
using Moq;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Integration)]
public class GeneratorIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public GeneratorIntegrationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthReady_ReturnsHealthy()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/ready");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.NotEmpty(body);
    }

    [Fact]
    public async Task Generate_ReturnsContent_WithApiKey()
    {
        var client = CreateClient(out var channel);

        var call = client.GenerateAsync(new GenerateRequest { RequestId = "r1", Prompt = "world" });
        var reply = await call.ResponseAsync;

        Assert.Equal("r1", reply.RequestId);
        Assert.Equal("test-model", reply.Model);
        Assert.NotEmpty(reply.Content);
        Assert.Equal(5, reply.Usage.InputTokens);
        Assert.False(reply.RuntimeTrace.StructuredOutputApplied);

        channel.Dispose();
    }

    [Fact]
    public async Task Generate_WithGenerationOptions_ReturnsContent()
    {
        var client = CreateClient(out var channel);

        var reply = await client.GenerateAsync(new GenerateRequest
        {
            RequestId = "generation-options",
            Prompt = "world",
            Generation = new GenerationOptions
            {
                Temperature = 0.4f,
                TopP = 0.9f,
                MaxOutputTokens = 4
            }
        }).ResponseAsync;

        Assert.Equal("generation-options", reply.RequestId);
        Assert.Equal("test-model", reply.Model);
        Assert.Equal("mocked response", reply.Content);

        channel.Dispose();
    }

    [Fact]
    public async Task Generate_OversizedPrompt_ReturnsHelpfulError()
    {
        var client = CreateClient(out var channel);

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GenerateAsync(new GenerateRequest
            {
                RequestId = "oversized",
                Prompt = new string('x', 30)
            }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("Prompt exceeds input budget", ex.Status.Detail);

        channel.Dispose();
    }

    [Fact]
    public async Task EstimateTokens_ReturnsBudgetDetails()
    {
        var client = CreateClient(out var channel);

        var reply = await client.EstimateTokensAsync(new EstimateTokensRequest
        {
            Prompt = "hello"
        }).ResponseAsync;

        Assert.Equal(5, reply.TokenCount);
        Assert.Equal(32, reply.ContextSize);
        Assert.Equal(8, reply.ReservedOutputTokens);
        Assert.Equal(24, reply.MaxAllowedInputTokens);
        Assert.True(reply.Fits);

        channel.Dispose();
    }

    [Fact]
    public async Task GetCapabilities_ReturnsLoadedModelConfiguration()
    {
        var client = CreateClient(out var channel);

        var reply = await client.GetCapabilitiesAsync(new GetCapabilitiesRequest()).ResponseAsync;

        Assert.Equal("test-model", reply.ModelId);
        Assert.Equal(32, reply.ContextSize);
        Assert.True(reply.SupportsStructuredOutput);
        Assert.True(reply.SupportsJsonOutput);
        Assert.False(reply.SupportsSpeculativeDecoding);
        Assert.Equal("sentencepiece", reply.TokenizerFamily);

        channel.Dispose();
    }

    [Fact]
    public async Task GetCapabilities_ReflectsHostedModelAndRuntimeConfigurationChanges()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HostedModel:ModelId"] = "alternate-model",
                    ["Llama:Native:ContextSize"] = "64"
                });
            });
            builder.ConfigureServices(services =>
            {
                var nativeMock = new Mock<ILlamaNative>();
                nativeMock.Setup(x => x.LoadModel(It.IsAny<string>()))
                    .Returns(LlamaModelHandle.FromIntPtr(new IntPtr(2)));
                nativeMock.Setup(x => x.GetModelMetadata(It.IsAny<LlamaModelHandle>()))
                    .Returns(new NativeModelMetadata(128, NativeTokenizerType.Bpe));
                nativeMock.Setup(x => x.CreateContext(It.IsAny<LlamaModelHandle>()))
                    .Returns(LlamaContextHandle.FromIntPtr(new IntPtr(2)));
                nativeMock.Setup(x => x.GetContextMetadata(It.IsAny<LlamaContextHandle>()))
                    .Returns(new NativeContextMetadata(64));
                nativeMock.Setup(x => x.CountTokens(It.IsAny<LlamaContextHandle>(), It.IsAny<string>()))
                    .Returns<LlamaContextHandle, string>((_, prompt) => prompt.Length);
                nativeMock.Setup(x => x.Infer(
                        It.IsAny<LlamaContextHandle>(),
                        It.IsAny<string>(),
                        It.IsAny<NativeInferenceResponseFormat>(),
                        It.IsAny<string?>(),
                        It.IsAny<NativeGenerationOptions?>(),
                        It.IsAny<CancellationToken>()))
                    .Returns<LlamaContextHandle, string, NativeInferenceResponseFormat, string?, NativeGenerationOptions?, CancellationToken>((_, prompt, responseFormat, _, _, _) =>
                    {
                        var content = responseFormat == NativeInferenceResponseFormat.Grammar
                            ? """{"alternate":true}"""
                            : "alternate response";

                        return
                        new NativeInferenceResult(
                            content,
                            prompt.Length,
                            content.Length,
                            prompt.Length + content.Length);
                    });

                services.RemoveAll<ILlamaNative>();
                services.AddSingleton(nativeMock.Object);
            });
        });

        var client = CreateClient(factory, out var channel);
        var reply = await client.GetCapabilitiesAsync(new GetCapabilitiesRequest()).ResponseAsync;

        Assert.Equal("alternate-model", reply.ModelId);
        Assert.Equal(64, reply.ContextSize);
        Assert.Equal("bpe", reply.TokenizerFamily);

        channel.Dispose();
    }

    [Fact]
    public async Task Generate_ReturnsUsageAndTrace()
    {
        var client = CreateClient(out var channel);

        var reply = await client.GenerateAsync(new GenerateRequest
        {
            RequestId = "generate",
            Prompt = "world"
        }).ResponseAsync;

        Assert.Equal("generate", reply.RequestId);
        Assert.Equal("test-model", reply.Model);
        Assert.NotEmpty(reply.Content);
        Assert.Equal(5, reply.Usage.InputTokens);
        Assert.Equal("mocked response".Length, reply.Usage.OutputTokens);
        Assert.Equal(reply.Usage.InputTokens + reply.Usage.OutputTokens, reply.Usage.TotalTokens);
        Assert.False(reply.RuntimeTrace.StructuredOutputApplied);
        Assert.False(reply.RuntimeTrace.StructuredOutputSatisfied);
        Assert.False(reply.RuntimeTrace.SpeculativeDecodingUsed);

        channel.Dispose();
    }

    [Fact]
    public async Task Generate_Json_ReturnsStructuredTrace()
    {
        var client = CreateClient(out var channel);

        var reply = await client.GenerateAsync(new GenerateRequest
        {
            RequestId = "json",
            Prompt = "world",
            ResponseFormat = new ResponseFormat { Type = "json" }
        }).ResponseAsync;

        Assert.Equal("""{"ok":true}""", reply.Content);
        Assert.True(reply.RuntimeTrace.StructuredOutputApplied);
        Assert.True(reply.RuntimeTrace.StructuredOutputSatisfied);

        channel.Dispose();
    }

    [Fact]
    public async Task Generate_InvalidPrompt_ReturnsNormalizedTrailers()
    {
        var client = CreateClient(out var channel);

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GenerateAsync(new GenerateRequest
            {
                RequestId = "invalid",
                Prompt = ""
            }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(RuntimeErrorMetadata.InvalidArgumentCode, ex.Trailers.Single(x => x.Key == RuntimeErrorMetadata.ErrorCodeTrailerName).Value);

        channel.Dispose();
    }

    private Generator.GeneratorClient CreateClient(out GrpcChannel channel)
    {
        channel = CreateChannel(_factory);
        return new Generator.GeneratorClient(channel);
    }

    private static Generator.GeneratorClient CreateClient(WebApplicationFactory<Program> factory, out GrpcChannel channel)
    {
        channel = CreateChannel(factory);
        return new Generator.GeneratorClient(channel);
    }

    private static GrpcChannel CreateChannel(WebApplicationFactory<Program> factory)
    {
        var httpClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        httpClient.DefaultRequestVersion = new Version(2, 0);
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        httpClient.DefaultRequestHeaders.Add(AuthConstants.AuthenticationScheme, TestWebApplicationFactory.ApiKey);

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpClient = httpClient });
    }
}

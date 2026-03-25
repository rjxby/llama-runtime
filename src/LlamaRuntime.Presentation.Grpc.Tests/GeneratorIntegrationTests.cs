using Microsoft.AspNetCore.Mvc.Testing;
using Grpc.Net.Client;
using Grpc.Core;
using LlamaRuntime.Presentation.Grpc.Auth;
using LlamaRuntime.Common.Tests;

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
    public async Task Generate_ReturnsResult_WithApiKey()
    {
        var httpClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        httpClient.DefaultRequestVersion = new Version(2, 0);
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;

        httpClient.DefaultRequestHeaders.Add(AuthConstants.AuthenticationScheme, TestWebApplicationFactory.ApiKey);

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpClient = httpClient });

        var client = new Generator.GeneratorClient(channel);

        var call = client.GenerateAsync(new GenerateRequest { RequestId = "r1", Prompt = "world" });
        var reply = await call.ResponseAsync;

        Assert.Equal("r1", reply.RequestId);
        Assert.NotEmpty(reply.Result);
    }

    [Fact]
    public async Task Generate_OversizedPrompt_ReturnsHelpfulError()
    {
        var httpClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        httpClient.DefaultRequestVersion = new Version(2, 0);
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        httpClient.DefaultRequestHeaders.Add(AuthConstants.AuthenticationScheme, TestWebApplicationFactory.ApiKey);

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpClient = httpClient });
        var client = new Generator.GeneratorClient(channel);

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GenerateAsync(new GenerateRequest
            {
                RequestId = "oversized",
                Prompt = new string('x', 30)
            }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("Prompt exceeds input budget", ex.Status.Detail);
    }

    [Fact]
    public async Task EstimateTokens_ReturnsBudgetDetails()
    {
        var httpClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        httpClient.DefaultRequestVersion = new Version(2, 0);
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        httpClient.DefaultRequestHeaders.Add(AuthConstants.AuthenticationScheme, TestWebApplicationFactory.ApiKey);

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpClient = httpClient });
        var client = new Generator.GeneratorClient(channel);

        var reply = await client.EstimateTokensAsync(new EstimateTokensRequest
        {
            Prompt = "hello"
        }).ResponseAsync;

        Assert.Equal(5, reply.TokenCount);
        Assert.Equal(32, reply.ContextSize);
        Assert.Equal(8, reply.ReservedOutputTokens);
        Assert.Equal(24, reply.MaxAllowedInputTokens);
        Assert.True(reply.Fits);
    }
}

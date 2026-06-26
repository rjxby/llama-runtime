using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using LlamaRuntime.Common.Tests;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.Auth;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.Inference;
using LlamaRuntime.Presentation.Grpc.ModelHosting;
using LlamaRuntime.Native;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Integration)]
public sealed partial class SecurityHardeningTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SecurityHardeningTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("-1", "32", "8", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.ContextSize))]
    [InlineData("32", "-1", "8", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.BatchSize))]
    [InlineData("32", "64", "8", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.BatchSize))]
    [InlineData("32", "8", "32", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.GenerationMaxNewTokens))]
    [InlineData("32", "8", "8", "20000000", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.InferenceBufferSize))]
    public async Task AddLlamaNative_InvalidOptions_FailOnStartup(
        string contextSize,
        string batchSize,
        string generationMaxNewTokens,
        string inferenceBufferSize,
        string expectedOptionName)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llama:Native:NativeLibraryPath"] = "test-native",
            ["Llama:Native:ContextSize"] = contextSize,
            ["Llama:Native:BatchSize"] = batchSize,
            ["Llama:Native:GenerationMaxNewTokens"] = generationMaxNewTokens,
            ["Llama:Native:InferenceBufferSize"] = inferenceBufferSize
        });
        builder.Services.AddLlamaNative();

        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(expectedOptionName, string.Join(Environment.NewLine, ex.Failures));
    }

    [Theory]
    [InlineData("0", "5", "00:00:01", "0", "429", nameof(RateLimiterOptions.TokenLimit))]
    [InlineData("20", "0", "00:00:01", "0", "429", nameof(RateLimiterOptions.TokensPerPeriod))]
    [InlineData("20", "5", "00:00:00", "0", "429", nameof(RateLimiterOptions.ReplenishmentPeriod))]
    [InlineData("20", "5", "00:00:01", "-1", "429", nameof(RateLimiterOptions.QueueLimit))]
    [InlineData("20", "5", "00:00:01", "0", "99", nameof(RateLimiterOptions.RejectionStatusCode))]
    public async Task AddAppRateLimiting_InvalidOptions_FailOnStartup(
        string tokenLimit,
        string tokensPerPeriod,
        string replenishmentPeriod,
        string queueLimit,
        string rejectionStatusCode,
        string expectedOptionName)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiter:TokenLimit"] = tokenLimit,
            ["RateLimiter:TokensPerPeriod"] = tokensPerPeriod,
            ["RateLimiter:ReplenishmentPeriod"] = replenishmentPeriod,
            ["RateLimiter:QueueLimit"] = queueLimit,
            ["RateLimiter:RejectionStatusCode"] = rejectionStatusCode
        });
        builder.Services.AddAppRateLimiting(builder.Configuration);

        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(expectedOptionName, string.Join(Environment.NewLine, ex.Failures));
    }


    [Fact]
    public async Task LoggingInterceptor_DoesNotLogPromptOrResponsePayloads()
    {
        var logSink = new TestLogSink();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddProvider(logSink);
            });
        });

        var client = CreateGrpcClient(factory);
        var prompt = "super secret prompt";

        var reply = await client.GenerateAsync(new GenerateRequest
        {
            RequestId = "redaction-check",
            Prompt = prompt
        }).ResponseAsync;

        var logs = string.Join(Environment.NewLine, logSink.Messages);
        Assert.NotEmpty(reply.Content);
        Assert.DoesNotContain(prompt, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(reply.Content, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimiter_UsesAuthenticatedIdentity_NotSpoofableHeaders()
    {
        using var factory = CreateRateLimitedFactory();
        using var client = factory.CreateClient();

        var first = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        first.Headers.Add(AuthConstants.AuthenticationScheme, TestWebApplicationFactory.ApiKey);
        var firstResponse = await client.SendAsync(first);

        var second = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        second.Headers.Add(AuthConstants.AuthenticationScheme, TestWebApplicationFactory.ApiKey);
        second.Headers.Add("x-spoofed-key", "other-partition");
        var secondResponse = await client.SendAsync(second);

        Assert.True(firstResponse.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
    }

    [Fact]
    public async Task RateLimiter_UsesAnonymousBucket_ForUnauthenticatedRequests()
    {
        using var factory = CreateRateLimitedFactory();
        using var client = factory.CreateClient();

        var firstResponse = await client.GetAsync("/health/live");
        var secondResponse = await client.GetAsync("/health/live");

        Assert.True(firstResponse.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
    }

    [Fact]
    public async Task RunningInference_IsNotTimedOutByAcquireTimeout()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<LlamaRuntime.Engine.Contracts.ILlamaProvider>();
        var hostedModel = new HostedModel(TestModelFactory.CreateNativeOptions());
        hostedModel.SetLoaded(TestModelFactory.CreateEngineModel("model.gguf"));

        provider.Setup(p => p.InferAsync(
                It.IsAny<LlamaRuntime.Engine.Contracts.IEngineModel>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                LlamaRuntime.Engine.Contracts.InferenceResponseFormat.Text,
                null))
            .Returns(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                return new LlamaRuntime.Engine.Contracts.InferenceResult("ok", 5, 2, 7);
            });

        var inferenceOptions = new InferenceOptions
        {
            ChannelCapacity = 1,
            WorkerCount = 1,
            AcquireTimeout = TimeSpan.FromMilliseconds(50),
            StartupWarmupPrompt = "Hello"
        };
        var queue = new InferenceWorkQueue(Options.Create(inferenceOptions));
        var coordinator = new QueuedInferenceCoordinator(provider.Object, queue);
        var worker = new QueuedInferenceWorker(
            queue,
            hostedModel,
            Options.Create(inferenceOptions),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QueuedInferenceWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var inFlight = coordinator.InferAsync("first", CancellationToken.None);

            await Task.Delay(150);
            gate.TrySetResult();

            var result = await inFlight;
            Assert.Equal("ok", result.Content);
        }
        finally
        {
            gate.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static Generator.GeneratorClient CreateGrpcClient(
        WebApplicationFactory<Program> factory,
        bool includeApiKey = true,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var httpClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        httpClient.DefaultRequestVersion = new Version(2, 0);
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        if (includeApiKey)
        {
            httpClient.DefaultRequestHeaders.Add(AuthConstants.AuthenticationScheme, TestWebApplicationFactory.ApiKey);
        }
        if (extraHeaders != null)
        {
            foreach (var header in extraHeaders)
            {
                httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
        }

        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpClient = httpClient });
        return new Generator.GeneratorClient(channel);
    }

    private WebApplicationFactory<Program> CreateRateLimitedFactory()
    {
        return new RateLimitedWebApplicationFactory();
    }
}

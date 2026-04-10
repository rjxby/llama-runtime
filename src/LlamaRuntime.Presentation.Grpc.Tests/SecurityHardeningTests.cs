using System.Collections.Concurrent;
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
using LlamaRuntime.Presentation.Grpc.Auth;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.ModelHosting;
using LlamaRuntime.Native;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Integration)]
public sealed class SecurityHardeningTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SecurityHardeningTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("-1", "32", "128", "8", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.ContextSize))]
    [InlineData("32", "-1", "128", "8", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.BatchSize))]
    [InlineData("32", "64", "128", "8", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.BatchSize))]
    [InlineData("32", "8", "128", "32", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.GenerationMaxNewTokens))]
    [InlineData("32", "8", "300000", "8", "1024", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.MaxTokens))]
    [InlineData("32", "8", "128", "8", "20000000", nameof(LlamaRuntime.Native.Contracts.Configuration.LlamaNativeOptions.InferenceBufferSize))]
    public async Task AddLlamaNative_InvalidOptions_FailOnStartup(
        string contextSize,
        string batchSize,
        string maxTokens,
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
            ["Llama:Native:MaxTokens"] = maxTokens,
            ["Llama:Native:GenerationMaxNewTokens"] = generationMaxNewTokens,
            ["Llama:Native:InferenceBufferSize"] = inferenceBufferSize
        });
        builder.Services.AddLlamaNative();

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
        Assert.NotEmpty(reply.Result);
        Assert.DoesNotContain(prompt, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(reply.Result, logs, StringComparison.Ordinal);
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
        var store = new HostedModelStore();
        store.SetLoaded(Mock.Of<LlamaRuntime.Engine.Contracts.IEngineModel>(m => m.Id == "model"));

        provider.Setup(p => p.InferAsync(
                It.IsAny<LlamaRuntime.Engine.Contracts.IEngineModel>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                return "ok";
            });

        var coordinator = new QueuedInferenceCoordinator(
            provider.Object,
            store,
            Options.Create(new InferenceOptions
            {
                ChannelCapacity = 1,
                WorkerCount = 1,
                AcquireTimeout = TimeSpan.FromMilliseconds(50)
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QueuedInferenceCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var inFlight = coordinator.InferAsync("first", CancellationToken.None);

            await Task.Delay(150);
            gate.TrySetResult();

            var result = await inFlight;
            Assert.Equal("ok", result);
        }
        finally
        {
            gate.TrySetResult();
            await coordinator.StopAsync(CancellationToken.None);
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

    private sealed class TestLogSink : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new TestLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class TestLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _messages;

            public TestLogger(ConcurrentQueue<string> messages)
            {
                _messages = messages;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _messages.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class RateLimitedWebApplicationFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["RateLimiter:TokenLimit"] = "1",
                        ["RateLimiter:TokensPerPeriod"] = "1",
                        ["RateLimiter:ReplenishmentPeriod"] = "01:00:00",
                        ["RateLimiter:QueueLimit"] = "0",
                        ["RateLimiter:RejectionStatusCode"] = "429"
                    })
                    .Build();

                services.AddAppRateLimiting(config);
            });

            base.ConfigureWebHost(builder);
        }
    }
}

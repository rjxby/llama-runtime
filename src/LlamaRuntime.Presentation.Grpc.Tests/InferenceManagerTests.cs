using Microsoft.Extensions.Options;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.Managers;
using LlamaRuntime.Common.Tests;
using Moq;

namespace LlamaRuntime.Presentation.Grpc.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public class InferenceManagerTests
{
    private readonly Mock<IOptions<InferenceOptions>> _optionsMock;
    private readonly InferenceOptions _options;

    public InferenceManagerTests()
    {
        _options = new InferenceOptions { ChannelCapacity = 2 };
        _optionsMock = new Mock<IOptions<InferenceOptions>>();
        _optionsMock.Setup(o => o.Value).Returns(_options);
    }

    [Fact]
    public async Task EnqueueAsync_ReturnsResult_WhenTaskCompleted()
    {
        var manager = new InferenceManager(_optionsMock.Object);
        var cts = new CancellationTokenSource();

        var enqueueTask = manager.EnqueueAsync("r1", "p1", cts.Token);

        await using var enumerator = manager.ReadRequestsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await enumerator.MoveNextAsync());
        var request = enumerator.Current;

        Assert.Equal("r1", request.RequestId);
        Assert.Equal("p1", request.Prompt);

        request.CompletionSource.SetResult("ok");
        var result = await enqueueTask;

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task EnqueueAsync_Waits_WhenChannelFull()
    {
        _options.ChannelCapacity = 1;
        var manager = new InferenceManager(_optionsMock.Object);
        var cts = new CancellationTokenSource();

        // Fill the channel
        var t1 = manager.EnqueueAsync("r1", "p1", cts.Token);

        // This should wait or eventually write
        var t2 = manager.EnqueueAsync("r2", "p2", cts.Token);

        await using var enumerator = manager.ReadRequestsAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("r1", enumerator.Current.RequestId);
        enumerator.Current.CompletionSource.SetResult("ok1");

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("r2", enumerator.Current.RequestId);
        enumerator.Current.CompletionSource.SetResult("ok2");

        Assert.Equal("ok1", await t1);
        Assert.Equal("ok2", await t2);
    }
}

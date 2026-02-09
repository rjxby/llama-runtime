using System.Threading.Channels;
using Microsoft.Extensions.Options;
using LlamaRuntime.Presentation.Grpc.Configuration;

namespace LlamaRuntime.Presentation.Grpc.Managers;

public sealed class InferenceManager : IInferenceManager
{
    private readonly Channel<InferenceRequest> _channel;

    public InferenceManager(IOptions<InferenceOptions> options)
    {
        var capacity = options.Value.ChannelCapacity;
        _channel = Channel.CreateBounded<InferenceRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public async Task<string> EnqueueAsync(string requestId, string prompt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new InferenceRequest(requestId, prompt, tcs, ct);

        try
        {
            if (!_channel.Writer.TryWrite(request))
            {
                await _channel.Writer.WriteAsync(request, ct).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("Inference queue is closed.");
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    public IAsyncEnumerable<InferenceRequest> ReadRequestsAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

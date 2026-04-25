using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;

namespace LlamaRuntime.Presentation.Grpc.Inference;

public sealed partial class InferenceWorkQueue
{
    private readonly Channel<IInferenceWorkItem> _channel;
    private readonly InferenceOptions _options;

    public InferenceWorkQueue(IOptions<InferenceOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (_options.ChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference channel capacity must be greater than zero.");
        }

        if (_options.AcquireTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Inference queue acquire timeout must be greater than zero.");
        }

        _channel = Channel.CreateBounded<IInferenceWorkItem>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    internal ValueTask<IInferenceWorkItem> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    internal void Complete() => _channel.Writer.TryComplete();

    internal async Task<T> EnqueueAsync<T>(
        InferenceOperation operation,
        Func<IEngineModel, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken,
        string? requestId)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var workItem = new InferenceWorkItem<T>(operation, work, cancellationToken, requestId);
        using var enqueueCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        enqueueCts.CancelAfter(_options.AcquireTimeout);

        try
        {
            await _channel.Writer.WriteAsync(workItem, enqueueCts.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new InferenceQueueRejectedException("Inference queue is closed because the runtime is stopping.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InferenceQueueRejectedException(
                $"Inference queue did not accept the request within {_options.AcquireTimeout}.",
                ex);
        }

        return await workItem.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

}

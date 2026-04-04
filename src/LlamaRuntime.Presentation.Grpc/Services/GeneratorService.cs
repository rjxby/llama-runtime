using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using LlamaRuntime.Engine.Contracts;
using Microsoft.Extensions.Options;
using LlamaRuntime.Native.Contracts.Configuration;

namespace LlamaRuntime.Presentation.Grpc.Services;

[Authorize]
public class GeneratorService : Generator.GeneratorBase
{
    private readonly ILogger<GeneratorService> _logger;
    private readonly IHostedModelStore _hostedModelStore;
    private readonly IInferenceExecutor _inferenceExecutor;
    private readonly LlamaNativeOptions _nativeOptions;

    public GeneratorService(
        ILogger<GeneratorService> logger,
        IHostedModelStore hostedModelStore,
        IInferenceExecutor inferenceExecutor,
        IOptions<LlamaNativeOptions> nativeOptions)
    {
        _logger = logger;
        _hostedModelStore = hostedModelStore;
        _inferenceExecutor = inferenceExecutor;
        _nativeOptions = nativeOptions?.Value ?? throw new ArgumentNullException(nameof(nativeOptions));
    }

    public override async Task<GenerateReply> Generate(GenerateRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "RequestId is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Prompt is required."));
        }

        var ct = context.CancellationToken;
        _logger.LogInformation("Generate request received (request_id={RequestId})", request.RequestId);

        if (!_hostedModelStore.TryGetLoadedModel(out _))
        {
            throw CreateModelStateException();
        }

        try
        {
            var result = await _inferenceExecutor.InferAsync(request.Prompt, ct).ConfigureAwait(false);

            return new GenerateReply
            {
                RequestId = request.RequestId,
                Result = result
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request cancelled"));
        }
        catch (PromptBudgetExceededException ex)
        {
            _logger.LogWarning(ex, "Prompt budget exceeded");
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (OutputBufferExceededException ex)
        {
            _logger.LogWarning(ex, "Inference output exceeded configured buffer");
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (InferenceQueueRejectedException ex)
        {
            _logger.LogWarning(ex, "Inference queue rejected request");
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (Exception ex) when (ex is ModelNotFoundException or LlamaRuntime.Engine.Contracts.InferenceException)
        {
            _logger.LogError(ex, "Inference failed");
            throw ex is ModelNotFoundException
                ? CreateModelStateException()
                : new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected server error");
            throw new RpcException(new Status(StatusCode.Internal, "Unexpected server error"));
        }
    }

    public override async Task<EstimateTokensReply> EstimateTokens(EstimateTokensRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Prompt is required."));
        }

        if (!_hostedModelStore.TryGetLoadedModel(out _))
        {
            throw CreateModelStateException();
        }

        var reservedOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
        var maxAllowedInputTokens = Math.Max(1, _nativeOptions.ContextSize - reservedOutputTokens);
        int tokenCount;

        try
        {
            tokenCount = await _inferenceExecutor.CountTokensAsync(request.Prompt, context.CancellationToken).ConfigureAwait(false);
        }
        catch (InferenceQueueRejectedException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (ModelNotFoundException)
        {
            throw CreateModelStateException();
        }

        return new EstimateTokensReply
        {
            TokenCount = tokenCount,
            ContextSize = _nativeOptions.ContextSize,
            ReservedOutputTokens = reservedOutputTokens,
            MaxAllowedInputTokens = maxAllowedInputTokens,
            Fits = tokenCount <= maxAllowedInputTokens
        };
    }

    private RpcException CreateModelStateException()
    {
        var snapshot = _hostedModelStore.GetSnapshot();
        var (statusCode, message) = snapshot.State switch
        {
            HostedModelState.Loading => (StatusCode.Unavailable, "Model is still loading."),
            HostedModelState.Failed => (StatusCode.Unavailable, snapshot.FailureMessage ?? "Model failed to load."),
            HostedModelState.Stopping => (StatusCode.Unavailable, "Model is stopping."),
            _ => (StatusCode.Unavailable, "Model not loaded.")
        };

        _logger.LogWarning("Model unavailable (state={State})", snapshot.State);
        return new RpcException(new Status(statusCode, message));
    }
}

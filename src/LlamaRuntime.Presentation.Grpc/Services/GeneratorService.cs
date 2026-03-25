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
    private readonly ILoadedModelAccessor _accessor;
    private readonly ILlamaProvider _provider;
    private readonly LlamaNativeOptions _nativeOptions;

    public GeneratorService(
        ILogger<GeneratorService> logger,
        ILoadedModelAccessor accessor,
        ILlamaProvider provider,
        IOptions<LlamaNativeOptions> nativeOptions)
    {
        _logger = logger;
        _accessor = accessor;
        _provider = provider;
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

        var model = _accessor.Model;
        if (model == null)
        {
            _logger.LogWarning("Model not loaded");
            throw new RpcException(new Status(StatusCode.Unavailable, "Model not loaded"));
        }

        try
        {
            var result = await _provider.InferAsync(model, request.Prompt, ct).ConfigureAwait(false);

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
        catch (Exception ex) when (ex is ModelNotFoundException or LlamaRuntime.Engine.Contracts.InferenceException)
        {
            _logger.LogError(ex, "Inference failed");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
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

        var model = _accessor.Model;
        if (model == null)
        {
            _logger.LogWarning("Model not loaded");
            throw new RpcException(new Status(StatusCode.Unavailable, "Model not loaded"));
        }

        var reservedOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
        var maxAllowedInputTokens = Math.Max(1, _nativeOptions.ContextSize - reservedOutputTokens);
        var tokenCount = await _provider.CountTokensAsync(model, request.Prompt, context.CancellationToken).ConfigureAwait(false);

        return new EstimateTokensReply
        {
            TokenCount = tokenCount,
            ContextSize = _nativeOptions.ContextSize,
            ReservedOutputTokens = reservedOutputTokens,
            MaxAllowedInputTokens = maxAllowedInputTokens,
            Fits = tokenCount <= maxAllowedInputTokens
        };
    }
}

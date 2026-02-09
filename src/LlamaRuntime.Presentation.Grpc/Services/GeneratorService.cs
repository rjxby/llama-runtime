using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Presentation.Grpc.Managers;

namespace LlamaRuntime.Presentation.Grpc.Services;

[Authorize]
public class GeneratorService : Generator.GeneratorBase
{
    private readonly ILogger<GeneratorService> _logger;
    private readonly ILoadedModelAccessor _accessor;
    private readonly IInferenceManager _inferenceManager;

    public GeneratorService(ILogger<GeneratorService> logger, ILoadedModelAccessor accessor, IInferenceManager inferenceManager)
    {
        _logger = logger;
        _accessor = accessor;
        _inferenceManager = inferenceManager;
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
            var result = await _inferenceManager.EnqueueAsync(request.RequestId, request.Prompt, ct);

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
        catch (ModelNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (InferenceException ex)
        {
            _logger.LogError(ex, "Inference failed");
            throw new RpcException(new Status(StatusCode.Internal, "Inference failed"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected server error");
            throw new RpcException(new Status(StatusCode.Internal, "Unexpected server error"));
        }
    }
}

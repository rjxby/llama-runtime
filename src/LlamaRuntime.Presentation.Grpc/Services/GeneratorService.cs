using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.Inference;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.Services;

[Authorize]
public sealed class GeneratorService : Generator.GeneratorBase
{
    private const string TextResponseFormat = "text";
    private const string JsonObjectResponseFormat = "json_object";
    private const float DefaultTemperature = 0.0f;
    private const float DefaultTopP = 1.0f;

    private readonly ILogger<GeneratorService> _logger;
    private readonly IInferenceCoordinator _inferenceCoordinator;
    private readonly LlamaNativeOptions _nativeOptions;
    private readonly IHostedRuntimeInfo _hostedRuntimeInfo;

    public GeneratorService(
        ILogger<GeneratorService> logger,
        IInferenceCoordinator inferenceCoordinator,
        IOptions<LlamaNativeOptions> nativeOptions,
        IHostedRuntimeInfo hostedRuntimeInfo)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inferenceCoordinator = inferenceCoordinator ?? throw new ArgumentNullException(nameof(inferenceCoordinator));
        _nativeOptions = nativeOptions?.Value ?? throw new ArgumentNullException(nameof(nativeOptions));
        _hostedRuntimeInfo = hostedRuntimeInfo ?? throw new ArgumentNullException(nameof(hostedRuntimeInfo));
    }

    public override async Task<GenerateReply> Generate(GenerateRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Generate request received (request_id={RequestId})", request.RequestId);

        try
        {
            var runtime = EnsureRuntimeLoaded();
            ValidateGenerateRequest(request, runtime);

            var inference = await _inferenceCoordinator.InferAsync(request.Prompt, context.CancellationToken, request.RequestId).ConfigureAwait(false);

            return new GenerateReply
            {
                RequestId = request.RequestId,
                Model = runtime.PublicModelId,
                Content = inference.Content,
                Usage = new Usage
                {
                    InputTokens = inference.InputTokens,
                    OutputTokens = inference.OutputTokens,
                    TotalTokens = inference.TotalTokens
                },
                RuntimeTrace = new RuntimeTrace
                {
                    StructuredOutputApplied = false,
                    StructuredOutputSatisfied = false,
                    SpeculativeDecodingUsed = false
                }
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw CreateRpcException(RuntimeErrorMetadata.CancelledCode, "Request cancelled.", StatusCode.Cancelled);
        }
        catch (Exception ex)
        {
            throw MapExceptionToRpcException(ex);
        }
    }

    public override async Task<EstimateTokensReply> EstimateTokens(EstimateTokensRequest request, ServerCallContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                throw CreateRpcException(RuntimeErrorMetadata.InvalidArgumentCode, "Prompt is required.", StatusCode.InvalidArgument);
            }

            var runtime = EnsureRuntimeLoaded();

            var tokenCount = await _inferenceCoordinator.CountTokensAsync(request.Prompt, context.CancellationToken).ConfigureAwait(false);
            var reservedOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
            var contextSize = runtime.EffectiveContextSize;
            var maxAllowedInputTokens = Math.Max(1, contextSize - reservedOutputTokens);

            return new EstimateTokensReply
            {
                TokenCount = tokenCount,
                ContextSize = contextSize,
                ReservedOutputTokens = reservedOutputTokens,
                MaxAllowedInputTokens = maxAllowedInputTokens,
                Fits = tokenCount <= maxAllowedInputTokens
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw CreateRpcException(RuntimeErrorMetadata.CancelledCode, "Request cancelled.", StatusCode.Cancelled);
        }
        catch (Exception ex)
        {
            throw MapExceptionToRpcException(ex);
        }
    }

    public override Task<GetCapabilitiesReply> GetCapabilities(GetCapabilitiesRequest request, ServerCallContext context)
    {
        try
        {
            var capabilities = EnsureRuntimeLoaded();

            return Task.FromResult(new GetCapabilitiesReply
            {
                ModelId = capabilities.PublicModelId,
                ContextSize = capabilities.EffectiveContextSize,
                SupportsStructuredOutput = capabilities.StructuredOutput.IsSupported,
                SupportsJsonObjectOutput = capabilities.JsonObjectOutput.IsSupported,
                SupportsSpeculativeDecoding = capabilities.SpeculativeDecoding.IsSupported,
                TokenizerFamily = capabilities.TokenizerFamily
            });
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MapExceptionToRpcException(ex);
        }
    }

    private void ValidateGenerateRequest(GenerateRequest request, HostedRuntimeInfo runtimeInfo)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw CreateRpcException(RuntimeErrorMetadata.InvalidArgumentCode, "RequestId is required.", StatusCode.InvalidArgument);
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw CreateRpcException(RuntimeErrorMetadata.InvalidArgumentCode, "Prompt is required.", StatusCode.InvalidArgument);
        }

        var responseFormat = request.ResponseFormat?.Type?.Trim();
        if (string.IsNullOrEmpty(responseFormat) || string.Equals(responseFormat, TextResponseFormat, StringComparison.Ordinal))
        {
            ValidateGenerationOptions(request.Generation, runtimeInfo);
            return;
        }

        if (string.Equals(responseFormat, JsonObjectResponseFormat, StringComparison.Ordinal))
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.UnsupportedResponseFormatCode,
                "Structured JSON object output is not supported by this runtime yet.",
                StatusCode.InvalidArgument);
        }

        throw CreateRpcException(
            RuntimeErrorMetadata.InvalidArgumentCode,
            $"ResponseFormat.Type must be one of '{TextResponseFormat}' or '{JsonObjectResponseFormat}'.",
            StatusCode.InvalidArgument);
    }

    private void ValidateGenerationOptions(GenerationOptions? generationOptions, HostedRuntimeInfo runtimeInfo)
    {
        if (generationOptions is null)
        {
            return;
        }

        if (generationOptions.HasTemperature && generationOptions.Temperature < 0.0f)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                "Generation.Temperature must be greater than or equal to 0.",
                StatusCode.InvalidArgument);
        }

        if (generationOptions.HasTopP && (generationOptions.TopP <= 0.0f || generationOptions.TopP > 1.0f))
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                "Generation.TopP must be greater than 0 and less than or equal to 1.",
                StatusCode.InvalidArgument);
        }

        if (generationOptions.HasMaxOutputTokens && generationOptions.MaxOutputTokens <= 0)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                "Generation.MaxOutputTokens must be greater than 0.",
                StatusCode.InvalidArgument);
        }

        var effectiveContextSize = runtimeInfo.EffectiveContextSize;

        if (generationOptions.HasMaxOutputTokens && generationOptions.MaxOutputTokens >= effectiveContextSize)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                $"Generation.MaxOutputTokens must be less than effective context size {effectiveContextSize}.",
                StatusCode.InvalidArgument);
        }

        var usesNonDefaultTemperature = generationOptions.HasTemperature && !AreClose(generationOptions.Temperature, DefaultTemperature);
        var usesNonDefaultTopP = generationOptions.HasTopP && !AreClose(generationOptions.TopP, DefaultTopP);
        var usesNonDefaultMaxOutputTokens =
            generationOptions.HasMaxOutputTokens &&
            generationOptions.MaxOutputTokens != _nativeOptions.GenerationMaxNewTokens;

        if (usesNonDefaultTemperature || usesNonDefaultTopP || usesNonDefaultMaxOutputTokens)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.UnsupportedGenerationOverridesCode,
                "Request-level generation overrides are not supported by this runtime yet. Omit Generation to use runtime defaults.",
                StatusCode.InvalidArgument);
        }
    }

    private HostedRuntimeInfo EnsureRuntimeLoaded()
    {
        var runtimeInfo = _hostedRuntimeInfo.GetRuntimeInfo();
        if (runtimeInfo.State == HostedModelState.Loaded)
        {
            return runtimeInfo;
        }

        var message = runtimeInfo.State switch
        {
            HostedModelState.Loading => "Model is still loading.",
            HostedModelState.WarmingUp => "Model is warming up.",
            HostedModelState.Failed => runtimeInfo.FailureMessage ?? "Model failed to load.",
            HostedModelState.Stopping => "Model is stopping.",
            _ => "Model not loaded."
        };

        throw CreateRpcException(RuntimeErrorMetadata.ModelUnavailableCode, message, StatusCode.Unavailable);
    }

    private static RpcException MapExceptionToRpcException(Exception exception) =>
        exception switch
        {
            PromptBudgetExceededException ex => CreateRpcException(RuntimeErrorMetadata.PromptBudgetExceededCode, ex.Message, StatusCode.InvalidArgument),
            OutputBufferExceededException ex => CreateRpcException(RuntimeErrorMetadata.OutputBufferExceededCode, ex.Message, StatusCode.ResourceExhausted),
            InferenceQueueRejectedException ex => CreateRpcException(RuntimeErrorMetadata.QueueRejectedCode, ex.Message, StatusCode.ResourceExhausted),
            ModelNotFoundException ex => CreateRpcException(RuntimeErrorMetadata.ModelUnavailableCode, ex.Message, StatusCode.Unavailable),
            EmptyInferenceOutputException ex => CreateRpcException(RuntimeErrorMetadata.InferenceFailedCode, ex.Message, StatusCode.Internal),
            InferenceException ex => CreateRpcException(RuntimeErrorMetadata.InferenceFailedCode, ex.Message, StatusCode.Internal),
            _ => CreateRpcException(RuntimeErrorMetadata.InferenceFailedCode, "Unexpected server error", StatusCode.Internal)
        };

    private static RpcException CreateRpcException(string code, string message, StatusCode statusCode)
    {
        var metadata = new Metadata
        {
            { RuntimeErrorMetadata.ErrorCodeTrailerName, code },
            { RuntimeErrorMetadata.ErrorMessageTrailerName, message }
        };

        return new RpcException(new Status(statusCode, message), metadata);
    }

    private static bool AreClose(float left, float right) => Math.Abs(left - right) < 0.0001f;
}

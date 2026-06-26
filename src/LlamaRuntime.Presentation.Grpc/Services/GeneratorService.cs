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
    private const string JsonResponseFormat = "json";
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
            var (responseFormat, generationOptions) = ValidateGenerateRequest(request, runtime);
            var jsonSchema = responseFormat == InferenceResponseFormat.Json
                ? request.ResponseFormat!.JsonSchema
                : null;

            var inference = await _inferenceCoordinator
                .InferAsync(request.Prompt, context.CancellationToken, request.RequestId, responseFormat, jsonSchema, generationOptions)
                .ConfigureAwait(false);

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
                    StructuredOutputApplied = responseFormat == InferenceResponseFormat.Json,
                    StructuredOutputSatisfied = responseFormat == InferenceResponseFormat.Json,
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
                SupportsJsonOutput = capabilities.JsonOutput.IsSupported,
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

    private (InferenceResponseFormat ResponseFormat, InferenceGenerationOptions GenerationOptions) ValidateGenerateRequest(
        GenerateRequest request,
        HostedRuntimeInfo runtimeInfo)
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
            if (!string.IsNullOrWhiteSpace(request.ResponseFormat?.JsonSchema))
            {
                throw CreateRpcException(
                    RuntimeErrorMetadata.InvalidArgumentCode,
                    "ResponseFormat.JsonSchema is only valid when ResponseFormat.Type is 'json'.",
                    StatusCode.InvalidArgument);
            }

            var generationOptions = ValidateGenerationOptions(request.Generation, runtimeInfo);
            return (InferenceResponseFormat.Text, generationOptions);
        }

        if (string.Equals(responseFormat, JsonResponseFormat, StringComparison.Ordinal))
        {
            if (!runtimeInfo.JsonOutput.IsSupported)
            {
                throw CreateRpcException(
                    RuntimeErrorMetadata.UnsupportedResponseFormatCode,
                    runtimeInfo.JsonOutput.Diagnostic,
                    StatusCode.InvalidArgument);
            }

            try
            {
                JsonStructuredOutput.ValidateSchema(request.ResponseFormat?.JsonSchema);
            }
            catch (ArgumentException ex)
            {
                throw CreateRpcException(
                    RuntimeErrorMetadata.InvalidArgumentCode,
                    ex.Message,
                    StatusCode.InvalidArgument);
            }

            var generationOptions = ValidateGenerationOptions(request.Generation, runtimeInfo);
            return (InferenceResponseFormat.Json, generationOptions);
        }

        throw CreateRpcException(
            RuntimeErrorMetadata.InvalidArgumentCode,
            $"ResponseFormat.Type must be one of '{TextResponseFormat}' or '{JsonResponseFormat}'.",
            StatusCode.InvalidArgument);
    }

    private InferenceGenerationOptions ValidateGenerationOptions(GenerationOptions? generationOptions, HostedRuntimeInfo runtimeInfo)
    {
        var maxOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
        var temperature = DefaultTemperature;
        var topP = DefaultTopP;

        if (generationOptions is null)
        {
            return new InferenceGenerationOptions(maxOutputTokens, temperature, topP);
        }

        if (generationOptions.HasTemperature)
        {
            temperature = generationOptions.Temperature;
        }

        if (generationOptions.HasTopP)
        {
            topP = generationOptions.TopP;
        }

        if (generationOptions.HasMaxOutputTokens)
        {
            maxOutputTokens = generationOptions.MaxOutputTokens;
        }

        if (!float.IsFinite(temperature) || temperature < 0.0f)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                "Generation.Temperature must be finite and greater than or equal to 0.",
                StatusCode.InvalidArgument);
        }

        if (!float.IsFinite(topP) || topP <= 0.0f || topP > 1.0f)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                "Generation.TopP must be finite, greater than 0, and less than or equal to 1.",
                StatusCode.InvalidArgument);
        }

        if (!AreClose(topP, DefaultTopP) && temperature <= 0.0f)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                "Generation.TopP requires Generation.Temperature to be greater than 0.",
                StatusCode.InvalidArgument);
        }

        if (maxOutputTokens <= 0)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                "Generation.MaxOutputTokens must be greater than 0.",
                StatusCode.InvalidArgument);
        }

        var configuredMaxOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
        if (maxOutputTokens > configuredMaxOutputTokens)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                $"Generation.MaxOutputTokens must be less than or equal to configured maximum {configuredMaxOutputTokens}.",
                StatusCode.InvalidArgument);
        }

        var effectiveContextSize = runtimeInfo.EffectiveContextSize;

        if (maxOutputTokens >= effectiveContextSize)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                $"Generation.MaxOutputTokens must be less than effective context size {effectiveContextSize}.",
                StatusCode.InvalidArgument);
        }

        return new InferenceGenerationOptions(maxOutputTokens, temperature, topP);
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
            StructuredOutputException ex => CreateRpcException(RuntimeErrorMetadata.StructuredOutputFailedCode, ex.Message, StatusCode.Internal),
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

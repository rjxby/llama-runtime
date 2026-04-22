using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.HostedServices;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.Services;

[Authorize]
public sealed class GeneratorService : Generator.GeneratorBase
{
    private const string TextResponseFormat = "text";
    private const string JsonObjectResponseFormat = "json_object";
    private const float DefaultTemperature = 0.0f;
    private const float DefaultTopP = 1.0f;
    private const string TokenizerFamily = "llama_cpp";

    private readonly ILogger<GeneratorService> _logger;
    private readonly IHostedModelStateReader _hostedModelReader;
    private readonly QueuedInferenceCoordinator _inferenceCoordinator;
    private readonly LlamaNativeOptions _nativeOptions;
    private readonly HostedModelOptions _hostedModelOptions;

    public GeneratorService(
        ILogger<GeneratorService> logger,
        IHostedModelStateReader hostedModelReader,
        QueuedInferenceCoordinator inferenceCoordinator,
        IOptions<LlamaNativeOptions> nativeOptions,
        IOptions<HostedModelOptions> hostedModelOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostedModelReader = hostedModelReader ?? throw new ArgumentNullException(nameof(hostedModelReader));
        _inferenceCoordinator = inferenceCoordinator ?? throw new ArgumentNullException(nameof(inferenceCoordinator));
        _nativeOptions = nativeOptions?.Value ?? throw new ArgumentNullException(nameof(nativeOptions));
        _hostedModelOptions = hostedModelOptions?.Value ?? throw new ArgumentNullException(nameof(hostedModelOptions));
    }

    public override async Task<GenerateReply> Generate(GenerateRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Generate request received (request_id={RequestId})", request.RequestId);

        try
        {
            ValidateGenerateRequest(request);
            EnsureModelLoaded();

            var inference = await _inferenceCoordinator.InferAsync(request.Prompt, context.CancellationToken, request.RequestId).ConfigureAwait(false);

            return new GenerateReply
            {
                RequestId = request.RequestId,
                Model = ResolveModelId(),
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

            EnsureModelLoaded();

            var tokenCount = await _inferenceCoordinator.CountTokensAsync(request.Prompt, context.CancellationToken).ConfigureAwait(false);
            var reservedOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
            var maxAllowedInputTokens = Math.Max(1, _nativeOptions.ContextSize - reservedOutputTokens);

            return new EstimateTokensReply
            {
                TokenCount = tokenCount,
                ContextSize = _nativeOptions.ContextSize,
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
            EnsureModelLoaded();

            return Task.FromResult(new GetCapabilitiesReply
            {
                ModelId = ResolveModelId(),
                ContextSize = _nativeOptions.ContextSize,
                SupportsStructuredOutput = false,
                SupportsJsonObjectOutput = false,
                SupportsSpeculativeDecoding = false,
                TokenizerFamily = TokenizerFamily
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

    private void ValidateGenerateRequest(GenerateRequest request)
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
            ValidateGenerationOptions(request.Generation);
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

    private void ValidateGenerationOptions(GenerationOptions? generationOptions)
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

        if (generationOptions.HasMaxOutputTokens && generationOptions.MaxOutputTokens >= _nativeOptions.ContextSize)
        {
            throw CreateRpcException(
                RuntimeErrorMetadata.InvalidArgumentCode,
                $"Generation.MaxOutputTokens must be less than configured context size {_nativeOptions.ContextSize}.",
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
                RuntimeErrorMetadata.InvalidArgumentCode,
                "Request-level generation overrides are not supported by this runtime yet. Omit Generation to use runtime defaults.",
                StatusCode.InvalidArgument);
        }
    }

    private void EnsureModelLoaded()
    {
        if (_hostedModelReader.TryGetLoadedModel(out _))
        {
            return;
        }

        var snapshot = _hostedModelReader.GetSnapshot();
        var message = snapshot.State switch
        {
            HostedModelState.Loading => "Model is still loading.",
            HostedModelState.WarmingUp => "Model is warming up.",
            HostedModelState.Failed => snapshot.FailureMessage ?? "Model failed to load.",
            HostedModelState.Stopping => "Model is stopping.",
            _ => "Model not loaded."
        };

        throw CreateRpcException(RuntimeErrorMetadata.ModelUnavailableCode, message, StatusCode.Unavailable);
    }

    private string ResolveModelId()
    {
        return _hostedModelOptions.ModelId;
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

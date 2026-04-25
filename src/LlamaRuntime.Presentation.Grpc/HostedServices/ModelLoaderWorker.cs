using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Presentation.Grpc.Configuration;
using LlamaRuntime.Presentation.Grpc.ModelHosting;

namespace LlamaRuntime.Presentation.Grpc.HostedServices;

public class ModelLoaderWorker : IHostedService
{
    private readonly ILogger<ModelLoaderWorker> _logger;
    private readonly ILlamaProvider _provider;
    private readonly IHostedModel _hostedModel;
    private readonly IHostedRuntimeInfo _hostedRuntimeInfo;
    private readonly string _hostedModelPath;
    private readonly string _hostedModelId;
    private readonly string _startupWarmupPrompt;

    public ModelLoaderWorker(
        ILogger<ModelLoaderWorker> logger,
        ILlamaProvider provider,
        IHostedModel hostedModel,
        IHostedRuntimeInfo hostedRuntimeInfo,
        IOptions<HostedModelOptions> options,
        IOptions<LlamaNativeOptions> nativeOptions,
        IOptions<InferenceOptions> inferenceOptions)
    {
        _logger = logger;
        _provider = provider;
        _hostedModel = hostedModel;
        _hostedRuntimeInfo = hostedRuntimeInfo;
        _hostedModelPath = options.Value.ModelPath;
        _hostedModelId = options.Value.ModelId;
        _ = nativeOptions?.Value ?? throw new ArgumentNullException(nameof(nativeOptions));
        _startupWarmupPrompt = inferenceOptions.Value.StartupWarmupPrompt;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ModelLoaderWorker starting. Loading model {Path}", _hostedModelPath);
        _hostedModel.SetLoading();

        if (string.IsNullOrEmpty(_hostedModelPath))
        {
            _logger.LogError("ModelPath not configured");
            _hostedModel.SetFailed(new InvalidOperationException("Model path must be configured"));
            throw new InvalidOperationException($"Model path must be configured");
        }

        IEngineModel? model = null;
        try
        {
            model = await _provider.LoadModelAsync(_hostedModelPath, cancellationToken).ConfigureAwait(false);
            _hostedModel.SetWarmingUp(model, _hostedModelId);
            LogDiscoveredModelMetadata();

            var warmup = await _provider.InferAsync(model, _startupWarmupPrompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Warm-up inference completed (len={Len})", warmup.Content.Length);

            _hostedModel.SetLoaded(model, _hostedModelId);
            LogCapabilityDiagnostics();
        }
        catch (Exception ex)
        {
            if (model != null)
            {
                try
                {
                    await _provider.UnloadModelAsync(model, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception unloadEx)
                {
                    _logger.LogWarning(unloadEx, "Failed unloading model after startup failure");
                }
            }

            _hostedModel.SetFailed(ex);
            _logger.LogError(ex, "Failed to load model during startup");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var snapshot = _hostedModel.GetSnapshot();
        _hostedModel.SetStopping();

        if (snapshot.Model != null)
        {
            await _provider.UnloadModelAsync(snapshot.Model, cancellationToken).ConfigureAwait(false);
        }

        _hostedModel.Reset();
    }

    private void LogDiscoveredModelMetadata()
    {
        var runtimeInfo = _hostedRuntimeInfo.GetRuntimeInfo();
        _logger.LogInformation(
            "Loaded model metadata for model {ModelId}: configured_runtime_context_size={ConfiguredRuntimeContextSize}, effective_runtime_context_size={EffectiveRuntimeContextSize}, training_context_size={TrainingContextSize}, tokenizer_family={TokenizerFamily}",
            runtimeInfo.PublicModelId,
            runtimeInfo.ConfiguredContextSize,
            runtimeInfo.EffectiveContextSize,
            runtimeInfo.TrainingContextSize ?? 0,
            runtimeInfo.TokenizerFamily);
    }

    private void LogCapabilityDiagnostics()
    {
        var runtimeInfo = _hostedRuntimeInfo.GetRuntimeInfo();
        _logger.LogInformation(
            "Runtime capabilities for model {ModelId}: context_size={ContextSize}, tokenizer_family={TokenizerFamily}, supports_structured_output={SupportsStructuredOutput}, supports_json_object_output={SupportsJsonObjectOutput}, supports_speculative_decoding={SupportsSpeculativeDecoding}",
            runtimeInfo.PublicModelId,
            runtimeInfo.EffectiveContextSize,
            runtimeInfo.TokenizerFamily,
            runtimeInfo.StructuredOutput.IsSupported,
            runtimeInfo.JsonObjectOutput.IsSupported,
            runtimeInfo.SpeculativeDecoding.IsSupported);

        LogUnsupportedCapability(runtimeInfo, "structured_output", runtimeInfo.StructuredOutput);
        LogUnsupportedCapability(runtimeInfo, "json_object_output", runtimeInfo.JsonObjectOutput);
        LogUnsupportedCapability(runtimeInfo, "speculative_decoding", runtimeInfo.SpeculativeDecoding);
    }

    private void LogUnsupportedCapability(
        HostedRuntimeInfo runtimeInfo,
        string capabilityName,
        RuntimeCapabilityStatus capability)
    {
        if (capability.IsSupported)
        {
            return;
        }

        _logger.LogInformation(
            "Runtime capability {CapabilityName} is unavailable for model {ModelId}: {Diagnostic}",
            capabilityName,
            runtimeInfo.PublicModelId,
            capability.Diagnostic);
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

public sealed class LlamaProvider : ILlamaProvider
{
    private readonly ILlamaNative _native;
    private readonly ILlamaContextManager _contextManager;
    private readonly ILogger<LlamaProvider> _logger;
    private readonly LlamaNativeOptions _nativeOptions;
    private bool _disposed;

    public LlamaProvider(
        ILlamaNative native,
        ILlamaContextManager contextManager,
        IOptions<LlamaNativeOptions> nativeOptions,
        ILogger<LlamaProvider> logger)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
        _nativeOptions = nativeOptions?.Value ?? throw new ArgumentNullException(nameof(nativeOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IEngineModel> LoadModelAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is null or empty", nameof(path));
        cancellationToken.ThrowIfCancellationRequested();

        LlamaModelHandle? modelHandle = null;
        LlamaContextHandle? probeContextHandle = null;
        try
        {
            modelHandle = _native.LoadModel(path);
            cancellationToken.ThrowIfCancellationRequested();

            var nativeMetadata = _native.GetModelMetadata(modelHandle);
            var probeContext = CreateProbeContext(modelHandle, path);
            probeContextHandle = probeContext.ContextHandle;
            cancellationToken.ThrowIfCancellationRequested();

            var contextMetadata = probeContext.Metadata;
            ValidateRequestedContext(path, contextMetadata.ContextSize);
            var metadata = CreateModelMetadata(nativeMetadata, contextMetadata);

            var model = new EngineModel(path, modelHandle, metadata);
            _contextManager.PrimeModelContext(model, probeContextHandle);
            probeContextHandle = null;
            modelHandle = null;
            _logger.LogInformation(
                "Model loaded (path={Path}, configured_context_size={ConfiguredContextSize}, actual_context_size={ActualContextSize}, training_context_size={TrainingContextSize}, tokenizer_type={TokenizerType})",
                path,
                _nativeOptions.ContextSize,
                metadata.ContextSize,
                metadata.TrainingContextSize ?? 0,
                metadata.TokenizerType);

            return Task.FromResult<IEngineModel>(model);
        }
        catch (Exception ex)
        {
            CleanupFailedLoad(path, probeContextHandle, modelHandle);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogError(ex, "Failed to load model from {Path}", path);
            if (ex is ModelLoadException)
            {
                throw;
            }

            throw new ModelLoadException($"Failed to load model from {path}", ex);
        }
    }

    public Task UnloadModelAsync(IEngineModel model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        _contextManager.ReleaseModelResources(model);

        try { model.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed disposing model {Path}", model.SourcePath); }
        return Task.CompletedTask;
    }

    public async Task<int> CountTokensAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (prompt == null) throw new ArgumentNullException(nameof(prompt));

        await using var session = await _contextManager.CreateSessionAsync(model, cancellationToken).ConfigureAwait(false);
        return await session.CountTokensAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InferenceResult> InferAsync(
        IEngineModel model,
        string prompt,
        CancellationToken cancellationToken = default,
        InferenceResponseFormat responseFormat = InferenceResponseFormat.Text,
        string? jsonSchema = null)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (prompt == null) throw new ArgumentNullException(nameof(prompt));

        try
        {
            await using var session = await _contextManager.CreateSessionAsync(model, cancellationToken).ConfigureAwait(false);
            return await InferContentAsync(model, session, prompt, cancellationToken, responseFormat, jsonSchema).ConfigureAwait(false);
        }
        catch (NativeException ex)
        {
            throw new InferenceException(CreateInferenceMessage(ex), ex);
        }
    }

    private async Task<InferenceResult> InferContentAsync(
        IEngineModel model,
        IInferenceSession session,
        string prompt,
        CancellationToken cancellationToken,
        InferenceResponseFormat responseFormat,
        string? jsonSchema)
    {
        try
        {
            var structuredOutput = responseFormat == InferenceResponseFormat.Json
                ? JsonStructuredOutput.Parse(jsonSchema)
                : null;
            var grammar = structuredOutput?.Grammar;
            var result = await session.InferAsync(prompt, cancellationToken, responseFormat, grammar).ConfigureAwait(false);
            if (responseFormat == InferenceResponseFormat.Json)
            {
                structuredOutput!.ValidateOutput(result.Content);
                return result;
            }

            if (string.IsNullOrWhiteSpace(result.Content))
            {
                throw new EmptyInferenceOutputException("Inference returned blank output for a text-generation request.");
            }

            return result;
        }
        catch (NativeException ex)
        {
            if (ex is NativeBufferTooSmallException)
            {
                throw new OutputBufferExceededException(CreateInferenceMessage(ex), ex);
            }

            if (ex is NativeInvalidArgumentException)
            {
                var reservedOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
                var effectiveContextSize = GetEffectiveContextSize(model);
                var maxInputTokens = Math.Max(1, effectiveContextSize - reservedOutputTokens);
                throw new PromptBudgetExceededException(
                    $"Prompt exceeds input budget: native tokenizer reported more than {maxInputTokens} allowed tokens (context {effectiveContextSize}, reserved output {reservedOutputTokens}).");
            }

            throw new InferenceException(CreateInferenceMessage(ex), ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LlamaProvider));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private static string CreateInferenceMessage(NativeException ex)
    {
        return ex switch
        {
            NativeOutOfMemoryException => "Inference failed because the native runtime ran out of memory.",
            NativeIOException or NativeInferException => "Inference failed in the native runtime.",
            NativeBufferTooSmallException => "Inference output exceeded the configured native buffer size.",
            _ => "Inference failed."
        };
    }

    private (LlamaContextHandle ContextHandle, NativeContextMetadata Metadata) CreateProbeContext(
        LlamaModelHandle modelHandle,
        string path)
    {
        LlamaContextHandle? contextHandle = null;
        try
        {
            contextHandle = _native.CreateContext(modelHandle);
            var metadata = _native.GetContextMetadata(contextHandle);
            return (contextHandle, metadata);
        }
        catch (Exception ex)
        {
            if (contextHandle != null)
            {
                RemoveProbeContext(path, contextHandle);
            }

            if (ex is ModelLoadException or OperationCanceledException)
            {
                throw;
            }

            throw new ModelLoadException($"Failed to determine actual runtime context size for {path}.", ex);
        }
    }

    private void CleanupFailedLoad(
        string path,
        LlamaContextHandle? probeContextHandle,
        LlamaModelHandle? modelHandle)
    {
        if (probeContextHandle != null)
        {
            RemoveProbeContext(path, probeContextHandle);
        }

        if (modelHandle != null)
        {
            try
            {
                _native.UnloadModel(modelHandle);
            }
            catch (Exception unloadEx)
            {
                _logger.LogWarning(unloadEx, "Failed unloading model after load failure from {Path}", path);
            }
        }
    }

    private void RemoveProbeContext(string path, LlamaContextHandle contextHandle)
    {
        try
        {
            _native.RemoveContext(contextHandle);
        }
        catch (Exception removeEx)
        {
            _logger.LogWarning(removeEx, "Failed releasing probe context while loading {Path}", path);
        }
    }

    private void ValidateRequestedContext(string path, int actualContextSize)
    {
        if (actualContextSize <= 0)
        {
            throw new ModelLoadException($"Loaded model from {path} did not report a valid runtime context size.");
        }

        if (actualContextSize != _nativeOptions.ContextSize)
        {
            throw new ModelLoadException(
                $"Configured context size {_nativeOptions.ContextSize} does not match actual created context size {actualContextSize} for {path}.");
        }
    }

    private int GetEffectiveContextSize(IEngineModel model) =>
        model.Metadata?.ContextSize > 0 ? model.Metadata.ContextSize : _nativeOptions.ContextSize;

    private static ModelMetadata CreateModelMetadata(
        NativeModelMetadata nativeMetadata,
        NativeContextMetadata contextMetadata)
    {
        return new ModelMetadata(
            contextMetadata.ContextSize,
            nativeMetadata.TokenizerType,
            nativeMetadata.TrainingContextSize > 0 ? nativeMetadata.TrainingContextSize : null);
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

public sealed class LlamaProvider : ILlamaProvider
{
    private readonly ILlamaNative _native;
    private readonly int _poolSize;
    private readonly ILogger<LlamaProvider> _logger;
    private readonly LlamaNativeOptions _nativeOptions;
    private bool _disposed;

    public LlamaProvider(
        ILlamaNative native,
        IOptions<InferenceOptions> inferenceOptions,
        IOptions<LlamaNativeOptions> nativeOptions,
        ILogger<LlamaProvider> logger)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _poolSize = inferenceOptions?.Value.WorkerCount ?? throw new ArgumentNullException(nameof(inferenceOptions));
        _nativeOptions = nativeOptions?.Value ?? throw new ArgumentNullException(nameof(nativeOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IEngineModel> LoadModelAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path is null or empty", nameof(path));
        }

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

            var model = new EngineModel(path, modelHandle, metadata, _native, _poolSize, probeContextHandle);
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

    public async Task UnloadModelAsync(IEngineModel model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await model.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<int> CountTokensAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (prompt == null)
        {
            throw new ArgumentNullException(nameof(prompt));
        }

        await using var session = await model.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        return await session.CountTokensAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InferenceResult> InferAsync(
        IEngineModel model,
        PreparedGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await using var session = await model.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            var format = request.Constraint == null ? InferenceResponseFormat.Text : InferenceResponseFormat.Json;
            var result = await session.InferAsync(request.Prompt, cancellationToken, format,
                request.Constraint?.Grammar, request.Generation).ConfigureAwait(false);
            if (request.Constraint != null)
            {
                request.Constraint.ValidateOutput(result.Content);
            }
            else if (string.IsNullOrWhiteSpace(result.Content))
            {
                throw new EmptyInferenceOutputException("Inference returned blank output for a text-generation request.");
            }

            return result;
        }
        catch (NativePromptBudgetExceededException ex)
        {
            var reservedOutputTokens = Math.Max(1, request.Generation.MaxOutputTokens);
            var effectiveContextSize = model.Metadata.ContextSize;
            var maxInputTokens = Math.Max(1, effectiveContextSize - reservedOutputTokens);
            throw new PromptBudgetExceededException(
                $"Prompt exceeds input budget: tokenizer reported {ex.PromptTokens} tokens but only {maxInputTokens} are allowed (context {effectiveContextSize}, reserved output {reservedOutputTokens}).");
        }
        catch (NativeBufferTooSmallException ex)
        {
            throw new OutputBufferExceededException(CreateInferenceMessage(ex), ex);
        }
        catch (NativeEmptyOutputException)
        {
            throw new EmptyInferenceOutputException("Inference returned blank output for a text-generation request.");
        }
        catch (NativeCancelledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (NativeException ex)
        {
            throw new InferenceException(CreateInferenceMessage(ex), ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LlamaProvider));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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

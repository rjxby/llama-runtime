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

        LlamaModelHandle modelHandle;
        try
        {
            modelHandle = _native.LoadModel(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model from {Path}", path);
            throw new ModelLoadException($"Failed to load model from {path}", ex);
        }

        var model = new EngineModel(path, modelHandle);
        _logger.LogInformation("Model loaded (path={Path})", path);
        return Task.FromResult<IEngineModel>(model);
    }

    public Task UnloadModelAsync(IEngineModel model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        _contextManager.ReleaseModelResources(model);

        try { model.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed disposing model {Model}", model.Id); }
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

    public async Task<string> InferAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (prompt == null) throw new ArgumentNullException(nameof(prompt));

        try
        {
            await using var session = await _contextManager.CreateSessionAsync(model, cancellationToken).ConfigureAwait(false);
            var promptTokens = await session.CountTokensAsync(prompt, cancellationToken).ConfigureAwait(false);
            var reservedOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
            var maxInputTokens = Math.Max(1, _nativeOptions.ContextSize - reservedOutputTokens);

            if (promptTokens > maxInputTokens)
            {
                throw new PromptBudgetExceededException(
                    $"Prompt exceeds input budget: {promptTokens} tokens > {maxInputTokens} allowed (context {_nativeOptions.ContextSize}, reserved output {reservedOutputTokens}).");
            }

            var result = await session.InferAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result))
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
}

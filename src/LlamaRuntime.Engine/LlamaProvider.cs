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

    public async Task<IEngineModel> LoadModelAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is null or empty", nameof(path));

        LlamaModelHandle modelHandle;
        try
        {
            modelHandle = await Task.Run(() => _native.LoadModel(path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model from {Path}", path);
            throw new ModelLoadException($"Failed to load model from {path}", ex);
        }

        var model = new EngineModel(path, modelHandle);
        _logger.LogInformation("Model loaded (path={Path})", path);
        return model;
    }

    public async Task UnloadModelAsync(IEngineModel model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) return;

        _contextManager.ReleaseModelResources(model);

        try { await Task.Run(() => model.Dispose(), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed disposing model {Model}", model.Id); }
    }

    public async Task<int> CountTokensAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (prompt == null) throw new ArgumentNullException(nameof(prompt));

        await using var session = await CreateSessionAsync(model, cancellationToken).ConfigureAwait(false);
        return await session.CountTokensAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> InferAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (prompt == null) throw new ArgumentNullException(nameof(prompt));

        try
        {
            await using var session = await CreateSessionAsync(model, cancellationToken).ConfigureAwait(false);
            var promptTokens = await session.CountTokensAsync(prompt, cancellationToken).ConfigureAwait(false);
            var reservedOutputTokens = Math.Max(1, _nativeOptions.GenerationMaxNewTokens);
            var maxInputTokens = Math.Max(1, _nativeOptions.ContextSize - reservedOutputTokens);

            if (promptTokens > maxInputTokens)
            {
                throw new PromptBudgetExceededException(
                    $"Prompt exceeds input budget: {promptTokens} tokens > {maxInputTokens} allowed (context {_nativeOptions.ContextSize}, reserved output {reservedOutputTokens}).");
            }

            return await session.InferAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (NativeException ex)
        {
            throw new InferenceException(CreateInferenceMessage(ex), ex);
        }
    }

    public async Task<IInferenceSession> CreateSessionAsync(IEngineModel model, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));

        if (_contextManager is IInferenceSessionFactory factory)
        {
            return await factory.CreateSessionAsync(model, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException("The context manager does not support sessions.");
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
            _ => "Inference failed."
        };
    }
}

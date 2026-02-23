using Microsoft.Extensions.Logging;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;

namespace LlamaRuntime.Engine;

public sealed class LlamaProvider : ILlamaProvider
{
    private readonly ILlamaNative _native;
    private readonly ILlamaContextManager _contextManager;
    private readonly ILogger<LlamaProvider> _logger;
    private bool _disposed;

    public LlamaProvider(ILlamaNative native, ILlamaContextManager contextManager, ILogger<LlamaProvider> logger)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
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

    public async Task<string> InferAsync(IEngineModel model, string prompt, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (model == null) throw new ArgumentNullException(nameof(model));

        await using var session = await CreateSessionAsync(model, cancellationToken).ConfigureAwait(false);
        return await session.InferAsync(prompt, cancellationToken).ConfigureAwait(false);
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
}

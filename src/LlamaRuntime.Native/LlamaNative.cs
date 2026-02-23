using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;

namespace LlamaRuntime.Native;

public sealed class LlamaNative : ILlamaNative
{
    private readonly ILogger<LlamaNative> _logger;
    private readonly NativeLoader _loader;
    private readonly LlamaNativeOptions _options;
    private bool _disposed;
    private const int BufferSize = 4096;

    public LlamaNative(IOptions<LlamaNativeOptions> options, ILogger<LlamaNative> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.NativeLibraryPath))
            throw new ArgumentException($"Library path must be provided", nameof(_options.NativeLibraryPath));

        _loader = new NativeLoader(_options.NativeLibraryPath, NativeMethods.LibraryLogicalName, _logger);


        NativeHandleReleaser.Register(
            releaseModel: ptr =>
            {
                try { NativeMethods.llama_unload_model(ptr); } catch { }
            },
            releaseContext: ptr =>
            {
                try { NativeMethods.llama_remove_context(ptr); } catch { }
            });

        _logger.LogInformation("NativeLibrary initialized");
    }

    public string GetVersion()
    {
        EnsureNotDisposed();
        var sb = new StringBuilder(BufferSize);
        var rc = NativeMethods.llama_adapter_get_version(sb, (UIntPtr)sb.Capacity);
        ThrowIfError(rc, "GetVersion");
        return sb.ToString();
    }

    public LlamaModelHandle LoadModel(string path)
    {
        EnsureNotDisposed();
        if (string.IsNullOrEmpty(path)) throw new NativeInvalidArgumentException("path is null or empty");

        var rc = NativeMethods.llama_load_model(path, out var ptr);
        ThrowIfError(rc, "LoadModel");
        if (ptr == IntPtr.Zero) throw new NativeLoadModelException("native returned null model pointer");
        return LlamaModelHandle.FromIntPtr(ptr);
    }

    public void UnloadModel(LlamaModelHandle model)
    {
        EnsureNotDisposed();
        model?.Dispose();
    }

    public LlamaContextHandle CreateContext(LlamaModelHandle model)
    {
        EnsureNotDisposed();
        if (model == null || model.IsInvalid) throw new NativeInvalidArgumentException("model is null or invalid");
        var rc = NativeMethods.llama_create_context(model.DangerousGetHandle(), _options.ContextSize, _options.BatchSize, out var ptr);
        ThrowIfError(rc, "CreateContext");
        if (ptr == IntPtr.Zero) throw new NativeLoadModelException("native returned null context pointer");
        return LlamaContextHandle.FromIntPtr(ptr);
    }

    public void RemoveContext(LlamaContextHandle ctx)
    {
        EnsureNotDisposed();
        ctx?.Dispose();
    }

    public void ResetContext(LlamaContextHandle ctx)
    {
        EnsureNotDisposed();
        if (ctx == null || ctx.IsInvalid) throw new NativeInvalidArgumentException("ctx is null or invalid");
        var rc = NativeMethods.llama_context_reset(ctx.DangerousGetHandle());
        ThrowIfError(rc, "ResetContext");
    }

    public string Infer(LlamaContextHandle ctx, string prompt)
    {
        EnsureNotDisposed();
        if (ctx == null || ctx.IsInvalid) throw new NativeInvalidArgumentException("ctx is null or invalid");
        if (prompt == null) throw new NativeInvalidArgumentException("prompt is null");

        var sb = new StringBuilder(BufferSize);
        var rc = NativeMethods.llama_infer(ctx.DangerousGetHandle(), prompt, sb, (UIntPtr)sb.Capacity);
        ThrowIfError(rc, "Infer");
        return sb.ToString();
    }

    private static void ThrowIfError(int code, string op)
    {
        if (code == (int)NativeError.Ok) return;
        var err = (NativeError)code;
        var msg = $"Native operation '{op}' failed: {err} ({code})";
        switch (err)
        {
            case NativeError.InvalidArgument: throw new NativeInvalidArgumentException(msg);
            case NativeError.NotInitialized: throw new NativeNotInitializedException(msg);
            case NativeError.LoadModel: throw new NativeLoadModelException(msg);
            case NativeError.OutOfMemory: throw new NativeOutOfMemoryException(msg);
            case NativeError.Infer: throw new NativeInferException(msg);
            case NativeError.NotImplemented: throw new NativeNotImplementedException(msg);
            case NativeError.NotFound: throw new NativeNotFoundException(msg);
            case NativeError.Io: throw new NativeIOException(msg);
            default: throw new NativeUnknownException(msg);
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeLibrary));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _loader.Dispose(); } catch { }
        _logger.LogInformation("NativeLibrary disposed");
    }
}
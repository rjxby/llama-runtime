using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;

namespace LlamaRuntime.Native;

public sealed class LlamaNative : ILlamaNative
{
    private static readonly NativeMethods.AbortCallback AbortCallback = IsCancellationRequested;

    private readonly ILogger<LlamaNative> _logger;
    private readonly LlamaNativeOptions _options;
    private bool _disposed;

    public LlamaNative(IOptions<LlamaNativeOptions> options, ILogger<LlamaNative> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.NativeLibraryPath))
        {
            throw new ArgumentException($"Library path must be provided", nameof(_options.NativeLibraryPath));
        }

        var loader = new NativeLoader(_options.NativeLibraryPath, NativeMethods.LibraryLogicalName, _logger);
        NativeRuntimeBindings.Initialize(loader.Handle);

        _logger.LogInformation("NativeLibrary initialized");
    }

    public string GetVersion()
    {
        EnsureNotDisposed();
        var sb = new StringBuilder(_options.InferenceBufferSize);
        var bufferSize = (UIntPtr)sb.Capacity;
        var rc = NativeMethods.llama_adapter_get_version(sb, bufferSize);
        ThrowIfError(rc, nameof(GetVersion));
        return sb.ToString();
    }

    public LlamaModelHandle LoadModel(string path)
    {
        EnsureNotDisposed();
        if (string.IsNullOrEmpty(path))
        {
            throw new NativeInvalidArgumentException($"{nameof(path)} is null or empty");
        }

        var rc = NativeMethods.llama_load_model(path, out var ptr);
        ThrowIfError(rc, nameof(LoadModel));
        if (ptr == IntPtr.Zero)
        {
            throw new NativeLoadModelException("native returned null model pointer");
        }

        return LlamaModelHandle.FromIntPtr(ptr);
    }

    public NativeModelMetadata GetModelMetadata(LlamaModelHandle model)
    {
        EnsureNotDisposed();
        if (model == null || model.IsInvalid)
        {
            throw new NativeInvalidArgumentException($"{nameof(model)} is null or invalid");
        }

        var rc = NativeMethods.llama_model_get_metadata(model, out var metadata);
        ThrowIfError(rc, nameof(GetModelMetadata));

        var tokenizerType = Enum.IsDefined(typeof(NativeTokenizerType), metadata.TokenizerType)
            ? (NativeTokenizerType)metadata.TokenizerType
            : NativeTokenizerType.Unknown;

        return new NativeModelMetadata(metadata.TrainingContextSize, tokenizerType);
    }

    public NativeContextMetadata GetContextMetadata(LlamaContextHandle context)
    {
        EnsureNotDisposed();
        if (context == null || context.IsInvalid)
        {
            throw new NativeInvalidArgumentException($"{nameof(context)} is null or invalid");
        }

        var rc = NativeMethods.llama_context_get_metadata(context, out var metadata);
        ThrowIfError(rc, nameof(GetContextMetadata));

        return new NativeContextMetadata(metadata.ContextSize);
    }

    public void UnloadModel(LlamaModelHandle model)
    {
        EnsureNotDisposed();
        model?.Dispose();
    }

    public LlamaContextHandle CreateContext(LlamaModelHandle model)
    {
        EnsureNotDisposed();
        if (model == null || model.IsInvalid)
        {
            throw new NativeInvalidArgumentException($"{nameof(model)} is null or invalid");
        }

        var rc = NativeMethods.llama_create_context(
            model,
            _options.ContextSize,
            _options.BatchSize,
            _options.GenerationMaxNewTokens,
            out var ptr);
        ThrowIfError(rc, nameof(CreateContext));
        if (ptr == IntPtr.Zero)
        {
            throw new NativeLoadModelException("native returned null context pointer");
        }

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
        if (ctx == null || ctx.IsInvalid)
        {
            throw new NativeInvalidArgumentException($"{nameof(ctx)} is null or invalid");
        }

        var rc = NativeMethods.llama_context_reset(ctx);
        ThrowIfError(rc, nameof(ResetContext));
    }

    public int CountTokens(LlamaContextHandle ctx, string prompt)
    {
        EnsureNotDisposed();
        if (ctx == null || ctx.IsInvalid)
        {
            throw new NativeInvalidArgumentException($"{nameof(ctx)} is null or invalid");
        }

        if (prompt == null)
        {
            throw new NativeInvalidArgumentException($"{nameof(prompt)} is null");
        }

        var rc = NativeMethods.llama_count_tokens(ctx, prompt, out var tokenCount);
        ThrowIfError(rc, nameof(CountTokens));
        return tokenCount;
    }

    public NativeInferenceResult Infer(
        LlamaContextHandle ctx,
        string prompt,
        NativeInferenceResponseFormat responseFormat = NativeInferenceResponseFormat.Text,
        string? grammar = null,
        NativeGenerationOptions? generationOptions = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (ctx == null || ctx.IsInvalid)
        {
            throw new NativeInvalidArgumentException($"{nameof(ctx)} is null or invalid");
        }

        if (prompt == null)
        {
            throw new NativeInvalidArgumentException($"{nameof(prompt)} is null");
        }

        cancellationToken.ThrowIfCancellationRequested();
        generationOptions ??= CreateDefaultGenerationOptions();
        ValidateGenerationOptions(generationOptions);

        var grammarPtr = IntPtr.Zero;
        GCHandle cancellationHandle = default;
        try
        {
            if (!string.IsNullOrEmpty(grammar))
            {
                grammarPtr = Marshal.StringToHGlobalAnsi(grammar);
            }

            var cancellationData = IntPtr.Zero;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationHandle = GCHandle.Alloc(new NativeCancellationState(cancellationToken));
                cancellationData = GCHandle.ToIntPtr(cancellationHandle);
            }

            var nativeResponseFormat = (int)responseFormat;
            var parameters = new NativeMethods.LlamaAdapterGenerationParams
            {
                MaxNewTokens = generationOptions.MaxNewTokens,
                Temperature = generationOptions.Temperature,
                TopP = generationOptions.TopP,
                Seed = uint.MaxValue,
                ResponseFormat = nativeResponseFormat,
                Grammar = grammarPtr,
                AbortCallback = cancellationToken.CanBeCanceled ? AbortCallback : null,
                AbortCallbackData = cancellationData
            };
            var sb = new StringBuilder(_options.InferenceBufferSize);
            var bufferSize = (UIntPtr)sb.Capacity;
            var rc = NativeMethods.llama_infer(
                ctx,
                prompt,
                in parameters,
                sb,
                bufferSize,
                out var result);

            if (rc == (int)NativeError.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (rc == (int)NativeError.PromptBudgetExceeded)
            {
                throw new NativePromptBudgetExceededException(result.PromptTokens);
            }

            ThrowIfError(rc, nameof(Infer));
            return new NativeInferenceResult(
                sb.ToString(),
                result.PromptTokens,
                result.OutputTokens,
                result.TotalTokens);
        }
        finally
        {
            if (grammarPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(grammarPtr);
            }

            if (cancellationHandle.IsAllocated)
            {
                cancellationHandle.Free();
            }
        }
    }

    private NativeGenerationOptions CreateDefaultGenerationOptions() =>
        new(
            Math.Max(1, _options.GenerationMaxNewTokens),
            GenerationOptionRules.DefaultTemperature,
            GenerationOptionRules.DefaultTopP);

    private sealed class NativeCancellationState
    {
        public NativeCancellationState(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }
    }

    private static bool IsCancellationRequested(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return false;
        }

        var handle = GCHandle.FromIntPtr(data);
        return handle.Target is NativeCancellationState state && state.CancellationToken.IsCancellationRequested;
    }

    private static void ValidateGenerationOptions(NativeGenerationOptions generationOptions)
    {
        if (!GenerationOptionRules.HasPositiveMaxTokens(generationOptions.MaxNewTokens))
        {
            throw new NativeInvalidArgumentException($"Generation {nameof(NativeGenerationOptions.MaxNewTokens)} must be greater than 0.");
        }

        if (!GenerationOptionRules.IsValidTemperature(generationOptions.Temperature))
        {
            throw new NativeInvalidArgumentException($"Generation {nameof(NativeGenerationOptions.Temperature)} must be finite and greater than or equal to 0.");
        }

        if (!GenerationOptionRules.IsValidTopP(generationOptions.TopP))
        {
            throw new NativeInvalidArgumentException($"Generation {nameof(NativeGenerationOptions.TopP)} must be finite, greater than 0, and less than or equal to 1.");
        }

        if (!GenerationOptionRules.IsTopPCompatibleWithTemperature(generationOptions.TopP, generationOptions.Temperature))
        {
            throw new NativeInvalidArgumentException($"Generation {nameof(NativeGenerationOptions.TopP)} requires {nameof(NativeGenerationOptions.Temperature)} to be greater than 0.");
        }
    }

    private static void ThrowIfError(int code, string op)
    {
        if (code == (int)NativeError.Ok)
        {
            return;
        }

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
            case NativeError.BufferTooSmall: throw new NativeBufferTooSmallException(msg);
            case NativeError.EmptyOutput: throw new NativeEmptyOutputException(msg);
            case NativeError.Cancelled: throw new NativeCancelledException(msg);
            default: throw new NativeUnknownException(msg);
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LlamaNative));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.LogInformation("Native wrapper disposed; library remains loaded for process lifetime");
    }
}

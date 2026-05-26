using Microsoft.Extensions.Options;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Native.Contracts.Configuration;

namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public sealed class HostedModel : IHostedModel, IHostedRuntimeInfo
{
    private const string StructuredOutputAvailableDiagnostic =
        "Structured output enforcement is available.";

    private const string JsonAvailableDiagnostic =
        "JSON output is available.";

    private const string SpeculativeDecodingUnavailableDiagnostic =
        "Speculative decoding is not implemented in this runtime yet.";

    private readonly Lock _gate = new();
    private readonly LlamaNativeOptions _nativeOptions;
    private HostedModelSnapshot _snapshot = new(HostedModelState.NotLoaded, null);

    public HostedModel(IOptions<LlamaNativeOptions> nativeOptions)
    {
        _nativeOptions = nativeOptions?.Value ?? throw new ArgumentNullException(nameof(nativeOptions));
    }

    public HostedModelSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public HostedRuntimeInfo GetRuntimeInfo()
    {
        var snapshot = GetSnapshot();
        var metadata = snapshot.Model?.Metadata;
        var tokenizerType = metadata?.TokenizerType ?? NativeTokenizerType.Unknown;

        return new HostedRuntimeInfo(
            snapshot.State,
            ResolvePublicModelId(snapshot),
            _nativeOptions.ContextSize,
            metadata?.ContextSize > 0 ? metadata.ContextSize : _nativeOptions.ContextSize,
            metadata?.TrainingContextSize,
            tokenizerType,
            FormatTokenizerFamily(tokenizerType),
            new RuntimeCapabilityStatus(true, StructuredOutputAvailableDiagnostic),
            new RuntimeCapabilityStatus(true, JsonAvailableDiagnostic),
            new RuntimeCapabilityStatus(false, SpeculativeDecodingUnavailableDiagnostic),
            snapshot.FailureMessage);
    }

    public bool TryGetLoadedModel(out IEngineModel? model)
    {
        lock (_gate)
        {
            model = _snapshot.Model;
            return _snapshot.State == HostedModelState.Loaded && model != null;
        }
    }

    public void SetLoading()
    {
        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.Loading, null);
        }
    }

    public void SetWarmingUp(IEngineModel model, string? configuredModelId = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.WarmingUp, model, ConfiguredModelId: configuredModelId);
        }
    }

    public void SetLoaded(IEngineModel model, string? configuredModelId = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.Loaded, model, ConfiguredModelId: configuredModelId);
        }
    }

    public void SetFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.Failed, null, exception.Message, _snapshot.ConfiguredModelId);
        }
    }

    public void SetStopping()
    {
        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.Stopping, _snapshot.Model, _snapshot.FailureMessage, _snapshot.ConfiguredModelId);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _snapshot = new HostedModelSnapshot(HostedModelState.NotLoaded, null);
        }
    }

    internal static string ResolvePublicModelId(HostedModelSnapshot hostedModel)
    {
        if (!string.IsNullOrWhiteSpace(hostedModel.ConfiguredModelId))
        {
            return hostedModel.ConfiguredModelId;
        }

        return DeriveModelIdFromSourcePath(hostedModel.Model?.SourcePath);
    }

    internal static string FormatTokenizerFamily(NativeTokenizerType tokenizerType) =>
        tokenizerType switch
        {
            NativeTokenizerType.SentencePiece => "sentencepiece",
            NativeTokenizerType.Bpe => "bpe",
            NativeTokenizerType.WordPiece => "wordpiece",
            NativeTokenizerType.Unigram => "unigram",
            NativeTokenizerType.Rwkv => "rwkv",
            NativeTokenizerType.Plamo2 => "plamo2",
            _ => "unknown"
        };

    private static string DeriveModelIdFromSourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return string.Empty;
        }

        var candidate = Path.GetFileNameWithoutExtension(sourcePath);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = Path.GetFileName(sourcePath);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        if (sourcePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return "model";
        }

        return sourcePath.Trim();
    }
}

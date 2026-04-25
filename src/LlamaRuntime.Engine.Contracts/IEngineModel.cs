namespace LlamaRuntime.Engine.Contracts;

/// <summary>
/// Represents a loaded model instance.
/// </summary>
public interface IEngineModel : IDisposable
{
    string SourcePath { get; }
    ModelMetadata? Metadata { get; }
}

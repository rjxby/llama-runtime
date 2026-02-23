namespace LlamaRuntime.Native.Contracts.Configuration;

public sealed class LlamaNativeOptions
{
    public const string SectionName = "Llama:Native";
    public required string NativeLibraryPath { get; set; }
    public int ContextSize { get; set; } = 4096;
    public int BatchSize { get; set; } = 512;
}

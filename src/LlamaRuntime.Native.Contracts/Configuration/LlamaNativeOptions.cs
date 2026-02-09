namespace LlamaRuntime.Native.Contracts.Configuration;

public sealed class LlamaNativeOptions
{
    public const string SectionName = "Llama:Native";
    public required string NativeLibraryPath { get; set; }
}

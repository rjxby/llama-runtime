namespace LlamaRuntime.Engine.Contracts.Configuration;

public sealed class LlamaProviderOptions
{
    public const string SectionName = "Llama:Provider";

    public int DefaultPoolSize { get; set; } = 1;
}

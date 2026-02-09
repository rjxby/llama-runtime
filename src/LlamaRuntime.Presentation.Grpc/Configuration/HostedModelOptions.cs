namespace LlamaRuntime.Presentation.Grpc.Configuration;

public class HostedModelOptions
{
    public const string SectionName = "HostedModel";
    public required string ModelPath { get; set; }
}

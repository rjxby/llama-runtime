namespace LlamaRuntime.Presentation.Grpc.Configuration;

public class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";
    public string[] Keys { get; set; } = [];
}

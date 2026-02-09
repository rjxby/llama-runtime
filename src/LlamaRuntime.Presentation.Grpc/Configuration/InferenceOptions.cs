namespace LlamaRuntime.Presentation.Grpc.Configuration;

public sealed class InferenceOptions
{
    public const string SectionName = "Inference";

    public int ChannelCapacity { get; set; } = 100;
    public int WorkerCount { get; set; } = 1;
}

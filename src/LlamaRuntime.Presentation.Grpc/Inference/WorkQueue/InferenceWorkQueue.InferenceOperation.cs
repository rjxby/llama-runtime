namespace LlamaRuntime.Presentation.Grpc.Inference;

public sealed partial class InferenceWorkQueue
{
    internal enum InferenceOperation
    {
        Infer = 0,
        CountTokens = 1
    }
}

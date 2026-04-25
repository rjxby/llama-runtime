namespace LlamaRuntime.Presentation.Grpc.ModelHosting;

public sealed record RuntimeCapabilityStatus(
    bool IsSupported,
    string Diagnostic);

namespace LlamaRuntime.Presentation.Grpc.Services;

internal static class RuntimeErrorMetadata
{
    public const string ErrorCodeTrailerName = "runtime-error-code";
    public const string ErrorMessageTrailerName = "runtime-error-message";

    public const string InvalidArgumentCode = "invalid_argument";
    public const string ModelUnavailableCode = "model_unavailable";
    public const string PromptBudgetExceededCode = "prompt_budget_exceeded";
    public const string OutputBufferExceededCode = "output_buffer_exceeded";
    public const string QueueRejectedCode = "queue_rejected";
    public const string UnsupportedResponseFormatCode = "unsupported_response_format";
    public const string StructuredOutputFailedCode = "structured_output_failed";
    public const string InferenceFailedCode = "inference_failed";
    public const string CancelledCode = "cancelled";
}

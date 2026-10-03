namespace LlamaRuntime.Native.Contracts;

public sealed class NativePromptBudgetExceededException : NativeException
{
    public int PromptTokens { get; }

    public NativePromptBudgetExceededException(int promptTokens)
        : base(NativeError.PromptBudgetExceeded, "Prompt exceeds the native context budget.") =>
        PromptTokens = promptTokens;
}

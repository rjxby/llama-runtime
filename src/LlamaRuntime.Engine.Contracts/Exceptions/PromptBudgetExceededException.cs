namespace LlamaRuntime.Engine.Contracts;

public sealed class PromptBudgetExceededException : InferenceException
{
    public PromptBudgetExceededException(string message) : base(message) { }
}

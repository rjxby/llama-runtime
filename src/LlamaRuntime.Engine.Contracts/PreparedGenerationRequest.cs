namespace LlamaRuntime.Engine.Contracts;

public sealed record PreparedGenerationRequest
{
    public PreparedGenerationRequest(string prompt, InferenceGenerationOptions generation,
        JsonStructuredOutput? constraint = null)
    {
        Prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
        Constraint = constraint;
    }

    public string Prompt { get; }
    public InferenceGenerationOptions Generation { get; }
    public JsonStructuredOutput? Constraint { get; }
}

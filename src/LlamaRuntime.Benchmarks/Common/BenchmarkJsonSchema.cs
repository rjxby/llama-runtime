namespace LlamaRuntime.Benchmarks.Common;

public static class BenchmarkJsonSchema
{
    public const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "summary": { "type": "string" },
            "category": { "type": "string" },
            "next_action": { "type": "string" }
          },
          "required": [ "title", "summary", "category", "next_action" ],
          "additionalProperties": false
        }
        """;

    public const string DefaultPrompt = """
        Extract structured information from this request.

        Request:
        A customer reports that their local inference service returns empty JSON objects during benchmark runs. They need to know whether this is a prompt problem, schema problem, or runtime bug.

        Return only a JSON object matching the provided JSON schema. The schema constrains the object structure; use the field meanings implied by their names.
        """;
}

using LlamaRuntime.Engine.Contracts;

namespace LlamaRuntime.Engine.Tests;

public sealed class JsonStructuredOutputTests
{
    [Theory]
    [InlineData("""{"type":"object","properties":{"title":{"type":"string"}},"additionalProperties":false}""", "required")]
    [InlineData("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""", "additionalProperties")]
    [InlineData("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title","extra"],"additionalProperties":false}""", "unknown property 'extra'")]
    [InlineData("""{"type":"object","properties":{"title":{"type":"string"},"summary":{"type":"string"}},"required":["title"],"additionalProperties":false}""", "property 'summary' must be listed")]
    [InlineData("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title","title"],"additionalProperties":false}""", "duplicate property 'title'")]
    [InlineData("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title"],"additionalProperties":true}""", "additionalProperties: false")]
    [InlineData("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title"],"additionalProperties":"false"}""", "additionalProperties: false")]
    [InlineData("""{"type":"object","required":[],"additionalProperties":false}""", "properties")]
    [InlineData("""{"type":"object","properties":{"item":{"type":"object","properties":{"name":{"type":"string"}},"additionalProperties":false}},"required":["item"],"additionalProperties":false}""", "required")]
    public void Parse_StrictObjectSchema_RejectsSchemasThatGrammarCannotEnforce(
        string schema,
        string expectedMessage)
    {
        var ex = Assert.Throws<ArgumentException>(() => JsonStructuredOutput.Parse(schema));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"value":{"type":"integer","enum":[1]}},"required":["value"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"value":{"type":"number","enum":[1.5]}},"required":["value"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"value":{"type":"boolean","enum":[true]}},"required":["value"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"value":{"type":"null","enum":[null]}},"required":["value"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"value":{"type":"array","items":{"type":"string"},"enum":[[]]}},"required":["value"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"value":{"type":"object","properties":{},"required":[],"additionalProperties":false,"enum":[{}]}},"required":["value"],"additionalProperties":false}""")]
    public void Parse_RejectsEnumOnNonStringSchemas(string schema)
    {
        var ex = Assert.Throws<ArgumentException>(() => JsonStructuredOutput.Parse(schema));

        Assert.Equal("Only string enum values are supported.", ex.Message);
    }

    [Fact]
    public void Parse_RejectsEmptyStringEnum()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "status": {
                  "type": "string",
                  "enum": []
                }
              },
              "required": [ "status" ],
              "additionalProperties": false
            }
            """;

        var ex = Assert.Throws<ArgumentException>(() => JsonStructuredOutput.Parse(schema));

        Assert.Equal("String enum must contain at least one value.", ex.Message);
    }

    [Fact]
    public void Parse_StrictObjectSchema_GeneratesMembersInPropertiesOrder()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "first": { "type": "string" },
                "second": { "type": "integer" }
              },
              "required": [ "first", "second" ],
              "additionalProperties": false
            }
            """;

        var structuredOutput = JsonStructuredOutput.Parse(schema);

        var firstIndex = structuredOutput.Grammar.IndexOf("first", StringComparison.Ordinal);
        var secondIndex = structuredOutput.Grammar.IndexOf("second", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, structuredOutput.Grammar);
        Assert.True(secondIndex > firstIndex, structuredOutput.Grammar);
    }

    [Fact]
    public void ValidateOutput_StrictObjectSchema_AllowsOutputPropertiesInAnyOrder()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "first": { "type": "string" },
                "second": { "type": "integer" }
              },
              "required": [ "first", "second" ],
              "additionalProperties": false
            }
            """;

        var structuredOutput = JsonStructuredOutput.Parse(schema);

        structuredOutput.ValidateOutput("""{"second":2,"first":"ok"}""");
    }

    [Fact]
    public void ValidateOutput_StrictObjectSchema_AllowsNestedObjects()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "item": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" },
                    "count": { "type": "integer" }
                  },
                  "required": [ "name", "count" ],
                  "additionalProperties": false
                }
              },
              "required": [ "item" ],
              "additionalProperties": false
            }
            """;

        var structuredOutput = JsonStructuredOutput.Parse(schema);

        structuredOutput.ValidateOutput("""{"item":{"count":3,"name":"ok"}}""");
    }

    [Fact]
    public void ValidateOutput_StrictObjectSchema_AllowsValidArrayItems()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "tags": {
                  "type": "array",
                  "items": { "type": "string" }
                }
              },
              "required": [ "tags" ],
              "additionalProperties": false
            }
            """;

        var structuredOutput = JsonStructuredOutput.Parse(schema);

        structuredOutput.ValidateOutput("""{"tags":["one","two"]}""");
    }

    [Theory]
    [InlineData("""{"second":2}""", "$.first is required.")]
    [InlineData("""{"first":"ok","second":2,"extra":true}""", "$.extra is not allowed by the JSON schema.")]
    [InlineData("""{"first":"ok","second":"2"}""", "$.second must be an integer.")]
    public void ValidateOutput_StrictObjectSchema_RejectsInvalidObjectOutput(
        string content,
        string expectedMessage)
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "first": { "type": "string" },
                "second": { "type": "integer" }
              },
              "required": [ "first", "second" ],
              "additionalProperties": false
            }
            """;

        var structuredOutput = JsonStructuredOutput.Parse(schema);

        var ex = Assert.Throws<StructuredOutputException>(() => structuredOutput.ValidateOutput(content));
        Assert.Equal(expectedMessage, ex.Message);
    }

    [Fact]
    public void ValidateOutput_StrictObjectSchema_RejectsInvalidArrayItem()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "scores": {
                  "type": "array",
                  "items": { "type": "number" }
                }
              },
              "required": [ "scores" ],
              "additionalProperties": false
            }
            """;

        var structuredOutput = JsonStructuredOutput.Parse(schema);

        var ex = Assert.Throws<StructuredOutputException>(() => structuredOutput.ValidateOutput("""{"scores":[1,"bad"]}"""));
        Assert.Equal("$.scores[1] must be a number.", ex.Message);
    }

    [Fact]
    public void ValidateOutput_StrictObjectSchema_RejectsStringEnumMismatch()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "status": {
                  "type": "string",
                  "enum": [ "ok", "failed" ]
                }
              },
              "required": [ "status" ],
              "additionalProperties": false
            }
            """;

        var structuredOutput = JsonStructuredOutput.Parse(schema);

        var ex = Assert.Throws<StructuredOutputException>(() => structuredOutput.ValidateOutput("""{"status":"pending"}"""));
        Assert.Equal("$.status must match one of the JSON schema enum values.", ex.Message);
    }

    [Fact]
    public void ValidateOutput_AnyObjectSchema_RejectsInvalidJson()
    {
        var structuredOutput = JsonStructuredOutput.Parse(null);

        var ex = Assert.Throws<StructuredOutputException>(() => structuredOutput.ValidateOutput("""{"missing":"""));
        Assert.Equal("Inference did not return a valid JSON object.", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")]
    public void ValidateOutput_RejectsNonObjectRoot(string? schema)
    {
        var structuredOutput = JsonStructuredOutput.Parse(schema);

        var ex = Assert.Throws<StructuredOutputException>(() => structuredOutput.ValidateOutput("""["not-object"]"""));
        Assert.Equal("Inference did not return a JSON object at the root.", ex.Message);
    }
}

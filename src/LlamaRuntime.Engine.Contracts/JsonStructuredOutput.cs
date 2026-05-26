using System.Text;
using System.Text.Json;

namespace LlamaRuntime.Engine.Contracts;

public sealed class JsonStructuredOutput
{
    public const string AnyObjectGrammar = """
        root ::= object
        array ::= "[" space ( value ("," space value)* )? "]" space
        boolean ::= ("true" | "false") space
        char ::= [^"\\\x7F\x00-\x1F] | [\\] (["\\bfnrt] | "u" [0-9a-fA-F]{4})
        decimal-part ::= [0-9]{1,16}
        integral-part ::= [0] | [1-9] [0-9]{0,15}
        null ::= "null" space
        number ::= ("-"? integral-part) ("." decimal-part)? ([eE] [-+]? integral-part)? space
        object ::= "{" space ( string ":" space value ("," space string ":" space value)* )? "}" space
        space ::= | " " | "\n"{1,2} [ \t]{0,20}
        string ::= "\"" char* "\"" space
        value ::= object | array | string | number | boolean | null
        """;

    private static readonly HashSet<string> AllowedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "type",
        "properties",
        "required",
        "additionalProperties",
        "items",
        "enum",
        "title",
        "description"
    };

    private readonly StrictSchemaNode? _schema;

    private JsonStructuredOutput(StrictSchemaNode? schema, string grammar)
    {
        _schema = schema;
        Grammar = grammar;
    }

    public string Grammar { get; }

    public static JsonStructuredOutput Parse(string? schemaJson)
    {
        using var document = TryParseSchemaDocument(schemaJson);
        if (document is null)
        {
            return new JsonStructuredOutput(null, AnyObjectGrammar);
        }

        var schema = StrictSchemaParser.Parse(document.RootElement);
        return new JsonStructuredOutput(schema, new GbnfBuilder().Build(schema));
    }

    public static void ValidateSchema(string? schemaJson) => Parse(schemaJson);

    public void ValidateOutput(string content)
    {
        JsonDocument outputDocument;
        try
        {
            outputDocument = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new StructuredOutputException("Inference did not return a valid JSON object.", ex);
        }

        using (outputDocument)
        {
            if (outputDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new StructuredOutputException("Inference did not return a JSON object at the root.");
            }

            if (_schema is null)
            {
                return;
            }

            StrictOutputValidator.Validate(outputDocument.RootElement, _schema);
        }
    }

    private static JsonDocument? TryParseSchemaDocument(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(schemaJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("ResponseFormat.JsonSchema must be valid JSON.", nameof(schemaJson), ex);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.EnumerateObject().Any())
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    private abstract record StrictSchemaNode;

    private sealed record ObjectSchema(
        IReadOnlyList<ObjectProperty> Properties,
        IReadOnlySet<string> PropertyNames) : StrictSchemaNode;

    private sealed record ObjectProperty(string Name, StrictSchemaNode Schema);

    private sealed record ArraySchema(StrictSchemaNode? Items) : StrictSchemaNode;

    private sealed record StringSchema(
        IReadOnlyList<string>? EnumValues,
        IReadOnlySet<string>? EnumValueLookup) : StrictSchemaNode;

    private sealed record NumberSchema() : StrictSchemaNode;

    private sealed record IntegerSchema() : StrictSchemaNode;

    private sealed record BooleanSchema() : StrictSchemaNode;

    private sealed record NullSchema() : StrictSchemaNode;

    private static class StrictSchemaParser
    {
        public static StrictSchemaNode Parse(JsonElement schema)
        {
            var parsed = ParseElement(schema, isRoot: true);
            return parsed;
        }

        private static StrictSchemaNode ParseElement(JsonElement schema, bool isRoot)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("JSON schema must be an object.");
            }

            foreach (var property in schema.EnumerateObject())
            {
                if (!AllowedSchemaKeywords.Contains(property.Name))
                {
                    throw new ArgumentException($"JSON schema keyword '{property.Name}' is not supported.");
                }
            }

            var type = GetRequiredType(schema);
            if (isRoot && type != "object")
            {
                throw new ArgumentException("JSON schema root type must be 'object'.");
            }

            if (type != "string" && schema.TryGetProperty("enum", out _))
            {
                throw new ArgumentException("Only string enum values are supported.");
            }

            return type switch
            {
                "object" => ParseObjectSchema(schema),
                "array" => ParseArraySchema(schema),
                "string" => ParseStringSchema(schema),
                "number" => new NumberSchema(),
                "integer" => new IntegerSchema(),
                "boolean" => new BooleanSchema(),
                "null" => new NullSchema(),
                _ => throw new ArgumentException($"JSON schema type '{type}' is not supported.")
            };
        }

        private static ObjectSchema ParseObjectSchema(JsonElement schema)
        {
            if (!schema.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Strict JSON object schemas must include a 'properties' object.");
            }

            if (!schema.TryGetProperty("required", out var required) ||
                required.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("Strict JSON object schemas must include a 'required' array.");
            }

            if (!schema.TryGetProperty("additionalProperties", out var additionalProperties) ||
                additionalProperties.ValueKind != JsonValueKind.False)
            {
                throw new ArgumentException("Strict JSON object schemas must include 'additionalProperties: false'.");
            }

            var schemaProperties = new List<ObjectProperty>();
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new ArgumentException("JSON schema 'properties' must not contain duplicate property names.");
                }

                schemaProperties.Add(new ObjectProperty(property.Name, ParseElement(property.Value, isRoot: false)));
            }

            var requiredNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException("JSON schema 'required' must be an array of strings.");
                }

                var name = item.GetString()!;
                if (!requiredNames.Add(name))
                {
                    throw new ArgumentException($"JSON schema 'required' contains duplicate property '{name}'.");
                }
            }

            foreach (var requiredName in requiredNames)
            {
                if (!propertyNames.Contains(requiredName))
                {
                    throw new ArgumentException($"JSON schema 'required' contains unknown property '{requiredName}'.");
                }
            }

            foreach (var propertyName in propertyNames)
            {
                if (!requiredNames.Contains(propertyName))
                {
                    throw new ArgumentException($"JSON schema property '{propertyName}' must be listed in 'required'.");
                }
            }

            return new ObjectSchema(schemaProperties, propertyNames);
        }

        private static ArraySchema ParseArraySchema(JsonElement schema)
        {
            var items = schema.TryGetProperty("items", out var itemSchema)
                ? ParseElement(itemSchema, isRoot: false)
                : null;

            return new ArraySchema(items);
        }

        private static StringSchema ParseStringSchema(JsonElement schema)
        {
            if (!schema.TryGetProperty("enum", out var values))
            {
                return new StringSchema(null, null);
            }

            if (values.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("Only string enum values are supported.");
            }

            if (values.GetArrayLength() == 0)
            {
                throw new ArgumentException("String enum must contain at least one value.");
            }

            var enumValues = new List<string>();
            var enumValueLookup = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException("Only string enum values are supported.");
                }

                var enumValue = value.GetString()!;
                enumValues.Add(enumValue);
                enumValueLookup.Add(enumValue);
            }

            return new StringSchema(enumValues, enumValueLookup);
        }

        private static string GetRequiredType(JsonElement schema)
        {
            if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("JSON schema must include a string 'type'.");
            }

            return type.GetString()!;
        }
    }

    private static class StrictOutputValidator
    {
        public static void Validate(JsonElement value, StrictSchemaNode schema) =>
            ValidateValue(value, schema, "$");

        private static void ValidateValue(JsonElement value, StrictSchemaNode schema, string path)
        {
            switch (schema)
            {
                case ObjectSchema objectSchema:
                    ValidateObjectValue(value, objectSchema, path);
                    break;
                case ArraySchema arraySchema:
                    ValidateArrayValue(value, arraySchema, path);
                    break;
                case StringSchema stringSchema:
                    ValidateStringValue(value, stringSchema, path);
                    break;
                case NumberSchema:
                    if (value.ValueKind != JsonValueKind.Number)
                    {
                        throw new StructuredOutputException($"{path} must be a number.");
                    }
                    break;
                case IntegerSchema:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
                    {
                        throw new StructuredOutputException($"{path} must be an integer.");
                    }
                    break;
                case BooleanSchema:
                    if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        throw new StructuredOutputException($"{path} must be a boolean.");
                    }
                    break;
                case NullSchema:
                    if (value.ValueKind != JsonValueKind.Null)
                    {
                        throw new StructuredOutputException($"{path} must be null.");
                    }
                    break;
            }
        }

        private static void ValidateObjectValue(JsonElement value, ObjectSchema schema, string path)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new StructuredOutputException($"{path} must be an object.");
            }

            foreach (var property in schema.Properties)
            {
                if (!value.TryGetProperty(property.Name, out var propertyValue))
                {
                    throw new StructuredOutputException($"{path}.{property.Name} is required.");
                }

                ValidateValue(propertyValue, property.Schema, $"{path}.{property.Name}");
            }

            foreach (var outputProperty in value.EnumerateObject())
            {
                if (!schema.PropertyNames.Contains(outputProperty.Name))
                {
                    throw new StructuredOutputException($"{path}.{outputProperty.Name} is not allowed by the JSON schema.");
                }
            }
        }

        private static void ValidateArrayValue(JsonElement value, ArraySchema schema, string path)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new StructuredOutputException($"{path} must be an array.");
            }

            if (schema.Items is null)
            {
                return;
            }

            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateValue(item, schema.Items, $"{path}[{index}]");
                index++;
            }
        }

        private static void ValidateStringValue(JsonElement value, StringSchema schema, string path)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new StructuredOutputException($"{path} must be a string.");
            }

            if (schema.EnumValueLookup is null)
            {
                return;
            }

            var text = value.GetString();
            if (!schema.EnumValueLookup.Contains(text!))
            {
                throw new StructuredOutputException($"{path} must match one of the JSON schema enum values.");
            }
        }
    }

    private sealed class GbnfBuilder
    {
        private readonly List<string> _rules = [];
        private int _nextRule;

        public string Build(StrictSchemaNode schema)
        {
            var builder = new StringBuilder();
            builder.Append("root ::= ");
            builder.Append(BuildRule(schema));
            builder.AppendLine();

            foreach (var rule in _rules)
            {
                builder.AppendLine(rule);
            }

            builder.AppendLine("""
                array-generic ::= "[" space ( value-generic ("," space value-generic)* )? "]" space
                boolean ::= ("true" | "false") space
                char ::= [^"\\\x7F\x00-\x1F] | [\\] (["\\bfnrt] | "u" [0-9a-fA-F]{4})
                decimal-part ::= [0-9]{1,16}
                integer ::= ("-"? integral-part) space
                integral-part ::= [0] | [1-9] [0-9]{0,15}
                null ::= "null" space
                number ::= ("-"? integral-part) ("." decimal-part)? ([eE] [-+]? integral-part)? space
                object-generic ::= "{" space ( string ":" space value-generic ("," space string ":" space value-generic)* )? "}" space
                space ::= | " " | "\n"{1,2} [ \t]{0,20}
                string ::= "\"" char* "\"" space
                value-generic ::= object-generic | array-generic | string | number | boolean | null
                """);

            return builder.ToString();
        }

        private string BuildRule(StrictSchemaNode schema)
        {
            var name = $"schema-{_nextRule++}";
            var rule = new StringBuilder();
            rule.Append(name);
            rule.Append(" ::= ");

            switch (schema)
            {
                case ObjectSchema objectSchema:
                    rule.Append(BuildObject(objectSchema));
                    break;
                case ArraySchema arraySchema:
                    var itemRule = arraySchema.Items is not null
                        ? BuildRule(arraySchema.Items)
                        : "value-generic";
                    rule.Append("\"[\" space ( ");
                    rule.Append(itemRule);
                    rule.Append(" (\",\" space ");
                    rule.Append(itemRule);
                    rule.Append(")* )? \"]\" space");
                    break;
                case StringSchema stringSchema:
                    if (stringSchema.EnumValues is not null)
                    {
                        rule.Append('(');
                        var first = true;
                        foreach (var value in stringSchema.EnumValues)
                        {
                            if (!first)
                            {
                                rule.Append(" | ");
                            }

                            first = false;
                            rule.Append(GbnfLiteral(JsonSerializer.Serialize(value)));
                        }
                        rule.Append(") space");
                    }
                    else
                    {
                        rule.Append("string");
                    }
                    break;
                case NumberSchema:
                    rule.Append("number");
                    break;
                case IntegerSchema:
                    rule.Append("integer");
                    break;
                case BooleanSchema:
                    rule.Append("boolean");
                    break;
                case NullSchema:
                    rule.Append("null");
                    break;
            }

            _rules.Add(rule.ToString());
            return name;
        }

        private string BuildObject(ObjectSchema schema)
        {
            if (schema.Properties.Count == 0)
            {
                return "\"{\" space \"}\" space";
            }

            var members = new List<string>();
            foreach (var property in schema.Properties)
            {
                var valueRule = BuildRule(property.Schema);
                members.Add($"{GbnfLiteral(JsonSerializer.Serialize(property.Name))} space \":\" space {valueRule}");
            }

            return "\"{\" space " + string.Join(" \",\" space ", members) + " \"}\" space";
        }

        private static string GbnfLiteral(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (var ch in value)
            {
                builder.Append(ch switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => ch.ToString()
                });
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}

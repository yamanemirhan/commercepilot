using System.Text.Json;

namespace Commerce.AI;

// Validates the flat scalar input schemas currently exposed by CommercePilot MCP.
// Unsupported schema shapes fail closed. Business rules/ownership still belong to MCP.
public static class ToolArguments
{
    public static void Validate(JsonElement schema, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            throw new ModelProtocolException("Expected an object tool schema and arguments.");
        if (schema.TryGetProperty("required", out var required))
            foreach (var key in required.EnumerateArray())
                if (!arguments.TryGetProperty(key.GetString()!, out _)) throw new ModelProtocolException("Missing required tool argument.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!names.Add(property.Name) || !properties.TryGetProperty(property.Name, out var definition))
                throw new ModelProtocolException("Unexpected or duplicate tool argument.");
            if (!definition.TryGetProperty("type", out var type)) throw new ModelProtocolException("Unsupported argument schema.");
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()).ToArray() : [type.GetString()];
            if (!types.Any(t => Matches(t, property.Value))) throw new ModelProtocolException("Incorrect tool argument type.");
            if (definition.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(v => JsonElement.DeepEquals(v, property.Value)))
                throw new ModelProtocolException("Invalid enumerated tool argument.");
            if (definition.TryGetProperty("format", out var format) && format.GetString() == "uuid" &&
                (!Guid.TryParse(property.Value.GetString(), out var id) || id == Guid.Empty))
                throw new ModelProtocolException("Expected a nonempty UUID tool argument.");
        }
    }

    private static bool Matches(string? type, JsonElement value) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
        _ => false
    };
}

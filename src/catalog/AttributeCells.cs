using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cmdb.Catalog;

/// <summary>
/// Attributes from table cells (#211): every property in a type's attribute schema with a non-empty cell of the same
/// name, read as the schema's type. Used by imports to plans and to production (#210).
/// </summary>
public static class AttributeCells
{
    /// <summary>
    /// The attributes the cells give, or null when there are none or the type has no schema. Values that cannot be read
    /// go to <paramref name="errors"/>. The result is not validated against the schema; that is the caller's next step.
    /// </summary>
    public static JsonObject? Read(JsonElement? schema, Func<string, string> cell, List<string> errors)
    {
        if (schema is not { } s || !s.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var result = new JsonObject();
        foreach (var property in properties.EnumerateObject())
        {
            var text = cell(property.Name).Trim();
            if (text.Length == 0)
            {
                continue;
            }
            var type = property.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            JsonNode? value = type switch
            {
                "integer" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
                "number" when double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                "boolean" when bool.TryParse(text, out var b) => b,
                "integer" or "number" or "boolean" => null,
                "array" or "object" => Parse(text),
                _ => text,
            };
            if (value is null)
            {
                errors.Add($"{property.Name}: {text} är inte {Swedish(type)}.");
                continue;
            }
            result[property.Name] = value;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>A JSON value from a cell, or null when it does not parse.</summary>
    public static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Swedish(string? type) => type switch
    {
        "integer" => "ett heltal",
        "number" => "ett tal",
        "boolean" => "true eller false",
        _ => "giltig JSON",
    };
}

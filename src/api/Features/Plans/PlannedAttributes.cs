using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Catalog;

namespace Cmdb.Api.Features.Plans;

/// <summary>
/// Attributes given with a new site or cable (#211): checked against the type's schema in the catalog before they
/// enter a plan, whether they come from the API, an agent or an import.
/// </summary>
internal static class PlannedAttributes
{
    /// <summary>Problems with <paramref name="attributes"/> for a new object of the type; empty when they fit or the type has no schema.</summary>
    public static IReadOnlyList<string> Problems(string objectType, string typeKey, JsonElement attributes) =>
        TypeCatalog.Current.ValidateAttributes(objectType, typeKey, attributes);

    /// <summary>
    /// The attributes an import row gives: every property in the type's schema with a non-empty cell of the same name,
    /// read as the schema's type. Null when there are none. Values that cannot be read go to <paramref name="errors"/>.
    /// </summary>
    public static JsonObject? FromCells(string objectType, string typeKey, Func<string, string> cell, List<string> errors)
    {
        if (TypeCatalog.Current.AttributeSchema(objectType, typeKey) is not { } schema
            || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
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
        if (result.Count == 0)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(result.ToJsonString());
        errors.AddRange(Problems(objectType, typeKey, doc.RootElement));
        return result;
    }

    private static JsonNode? Parse(string text)
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

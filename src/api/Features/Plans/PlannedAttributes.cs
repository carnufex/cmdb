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
        if (AttributeCells.Read(TypeCatalog.Current.AttributeSchema(objectType, typeKey), cell, errors) is not { } result)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(result.ToJsonString());
        errors.AddRange(Problems(objectType, typeKey, doc.RootElement));
        return result;
    }
}

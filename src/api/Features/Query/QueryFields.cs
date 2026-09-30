using Cmdb.Api.Auth;
using System.Text.Json;
using Cmdb.Catalog;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Query;

/// <summary>An attribute that equipment of a category can carry, from the catalog's JSON Schemas.</summary>
/// <param name="Type">string, number or integer.</param>
/// <param name="Values">Allowed values when the schema has an enum.</param>
public sealed record AttributeField(string Key, string Type, IReadOnlyList<JsonElement>? Values);

public sealed record CategoryField(string Key, IReadOnlyList<AttributeField> Attributes);

public sealed record TypeField(string Key, string Manufacturer, string Model, string Category);

public sealed record CableTypeField(string Key, string Name, string Medium, int Conductors);

/// <summary>What advanced search can filter on. The UI builds its pickers from this.</summary>
public sealed record QueryFields(
    IReadOnlyList<string> SiteTypes,
    IReadOnlyList<string> Lifecycles,
    IReadOnlyList<string> ServiceTypes,
    IReadOnlyList<CategoryField> Categories,
    IReadOnlyList<TypeField> Types,
    IReadOnlyList<CableTypeField>? CableTypes = null);

public sealed class QueryFieldsEndpoint(RequestDb db, TypeCatalog catalog) : EndpointWithoutRequest<QueryFields>
{
    public static readonly string[] Lifecycles = ["planned", "under_construction", "in_service", "decommissioning", "removed"];

    public override void Configure() => Get("/query/fields");

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(await LoadAsync(db, catalog, ct), ct);
    }

    /// <summary>Also used by the MCP tool <c>describe_catalog</c> (#61).</summary>
    internal static async Task<QueryFields> LoadAsync(NpgsqlDataSource db, TypeCatalog catalog, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var siteTypes = await Distinct(conn, "SELECT DISTINCT site_type FROM site ORDER BY 1", ct);
        var serviceTypes = await Distinct(conn, "SELECT DISTINCT service_type FROM service ORDER BY 1", ct);
        return new QueryFields(siteTypes, Lifecycles, serviceTypes, Categories(catalog), Types(catalog),
            [.. catalog.CableTypes.OrderBy(t => t.ConductorCount).ThenBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => new CableTypeField(t.Key, t.Name, t.Medium, t.ConductorCount))]);
    }

    internal static IReadOnlyList<CategoryField> Categories(TypeCatalog catalog) =>
        [.. catalog.Types
            .GroupBy(t => t.Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CategoryField(g.Key, [.. g
                .SelectMany(t => Attributes(t.Attributes))
                .GroupBy(a => a.Key)
                .Select(a => a.First() with { Values = MergeValues(a) })
                .OrderBy(a => a.Key, StringComparer.Ordinal)]))];

    internal static IReadOnlyList<TypeField> Types(TypeCatalog catalog) =>
        [.. catalog.Types
            .OrderBy(t => t.Category, StringComparer.Ordinal).ThenBy(t => t.Model, StringComparer.Ordinal)
            .Select(t => new TypeField(t.Key, t.Manufacturer, t.Model, t.Category))];

    /// <summary>Every attribute key any catalog type knows; advanced search accepts only these.</summary>
    internal static IReadOnlySet<string> AttributeKeys(TypeCatalog catalog) =>
        catalog.Types.SelectMany(t => Attributes(t.Attributes)).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<AttributeField> Attributes(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                var list = values.EnumerateArray().Select(v => v.Clone()).ToList();
                var type = list.All(v => v.ValueKind == JsonValueKind.Number) ? "number" : "string";
                yield return new AttributeField(property.Name, type, list);
            }
            else if (property.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                yield return new AttributeField(property.Name, type.GetString() == "integer" ? "number" : type.GetString()!, null);
            }
        }
    }

    private static List<JsonElement>? MergeValues(IEnumerable<AttributeField> fields)
    {
        var all = fields.Where(f => f.Values is not null).SelectMany(f => f.Values!).ToList();
        return all.Count == 0 ? null : [.. all.DistinctBy(v => v.GetRawText()).OrderBy(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0).ThenBy(v => v.ToString(), StringComparer.Ordinal)];
    }

    private static async Task<IReadOnlyList<string>> Distinct(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }
}

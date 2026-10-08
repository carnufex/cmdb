using Cmdb.Api.Auth;
using System.Text.Json;
using Cmdb.Catalog;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Query;

/// <summary>An attribute that equipment of a category can carry, from the catalog's JSON Schemas.</summary>
/// <param name="Type">string, number or integer.</param>
/// <param name="Values">Allowed values when the schema has an enum.</param>
/// <param name="Title">The display name from the schema's <c>title</c> (#211), when it has one.</param>
public sealed record AttributeField(string Key, string Type, IReadOnlyList<JsonElement>? Values, string? Title = null);

/// <param name="Name">The display name from the catalog (#208).</param>
/// <param name="Roles">What the code treats the category as: card, termination or power.</param>
public sealed record CategoryField(string Key, IReadOnlyList<AttributeField> Attributes, string? Name = null, IReadOnlyList<string>? Roles = null);

/// <summary>A site type or equipment category from the catalog with its name and roles (#208).</summary>
/// <param name="Attributes">The fields of the type's attribute schema (#211); null when its attributes are free.</param>
public sealed record KindField(string Key, string Name, IReadOnlyList<string> Roles, IReadOnlyList<AttributeField>? Attributes = null);

public sealed record TypeField(string Key, string Manufacturer, string Model, string Category);

public sealed record CableTypeField(string Key, string Name, string Medium, int Conductors, IReadOnlyList<AttributeField>? Attributes = null);

/// <summary>What advanced search can filter on. The UI builds its pickers from this.</summary>
public sealed record QueryFields(
    IReadOnlyList<string> SiteTypes,
    IReadOnlyList<string> Lifecycles,
    IReadOnlyList<string> ServiceTypes,
    IReadOnlyList<CategoryField> Categories,
    IReadOnlyList<TypeField> Types,
    IReadOnlyList<CableTypeField>? CableTypes = null,
    IReadOnlyList<KindField>? SiteTypeDetails = null,
    IReadOnlyList<KindField>? ServiceTypeDetails = null);

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
        // The catalog's site types in its order, then any other type the data holds.
        var inData = await Distinct(conn, "SELECT DISTINCT site_type FROM site ORDER BY 1", ct);
        var siteTypes = catalog.SiteTypes.Select(t => t.Key).Concat(inData.Where(t => catalog.FindSiteType(t) is null)).ToList();
        var inServices = await Distinct(conn, "SELECT DISTINCT service_type FROM service ORDER BY 1", ct);
        var serviceTypes = catalog.ServiceTypes.Select(t => t.Key).Concat(inServices.Where(t => catalog.FindServiceType(t) is null)).ToList();
        return new QueryFields(siteTypes, Lifecycles, serviceTypes, Categories(catalog), Types(catalog),
            [.. catalog.CableTypes.OrderBy(t => t.ConductorCount).ThenBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => new CableTypeField(t.Key, t.Name, t.Medium, t.ConductorCount, Fields(t.Attributes)))],
            SiteTypeFields(catalog), ServiceTypeFields(catalog));
    }

    internal static IReadOnlyList<KindField> SiteTypeFields(TypeCatalog catalog) =>
        [.. catalog.SiteTypes.Select(t => new KindField(t.Key, t.Name, t.Roles, Fields(t.Attributes)))];

    internal static IReadOnlyList<KindField> ServiceTypeFields(TypeCatalog catalog) =>
        [.. catalog.ServiceTypes.Select(t => new KindField(t.Key, t.Name, [], Fields(t.Attributes)))];

    /// <summary>The fields of an optional attribute schema (#211), or null when there is none.</summary>
    internal static IReadOnlyList<AttributeField>? Fields(JsonElement? schema) =>
        schema is { } s ? [.. Attributes(s)] : null;

    /// <summary>Every attribute key a site type's schema knows; site conditions in advanced search accept only these.</summary>
    internal static IReadOnlySet<string> SiteAttributeKeys(TypeCatalog catalog) =>
        catalog.SiteTypes.SelectMany(t => Fields(t.Attributes) ?? []).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every category in the catalog, with the attributes its models know.</summary>
    internal static IReadOnlyList<CategoryField> Categories(TypeCatalog catalog)
    {
        var withTypes = CategoriesWithTypes(catalog).ToDictionary(c => c.Key, StringComparer.Ordinal);
        return [.. catalog.Categories
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => (withTypes.GetValueOrDefault(c.Key) ?? new CategoryField(c.Key, [])) with { Name = c.Name, Roles = c.Roles })];
    }

    private static IReadOnlyList<CategoryField> CategoriesWithTypes(TypeCatalog catalog) =>
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

    internal static IEnumerable<AttributeField> Attributes(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }
        foreach (var property in properties.EnumerateObject())
        {
            var title = property.Value.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (property.Value.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                var list = values.EnumerateArray().Select(v => v.Clone()).ToList();
                var type = list.All(v => v.ValueKind == JsonValueKind.Number) ? "number" : "string";
                yield return new AttributeField(property.Name, type, list, title);
            }
            else if (property.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                yield return new AttributeField(property.Name, type.GetString() == "integer" ? "number" : type.GetString()!, null, title);
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

using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Query;
using Cmdb.Catalog;
using FastEndpoints;
using FluentValidation;
using Npgsql;

namespace Cmdb.Api.Features.Grid;

public sealed class GridRequest
{
    /// <summary>site or equipment: the sites themselves, or the equipment on them.</summary>
    public string Kind { get; set; } = "site";

    public long[] SiteIds { get; set; } = [];
}

public sealed class GridValidator : Validator<GridRequest>
{
    public GridValidator()
    {
        RuleFor(r => r.Kind).Must(k => k is "site" or "equipment").WithMessage("kind is site or equipment.");
        RuleFor(r => r.SiteIds).Must(s => s.Length is > 0 and <= GridEndpoint.MaxSites).WithMessage($"Give 1–{GridEndpoint.MaxSites} sites.");
    }
}

/// <param name="Values">Allowed values, when the schema has an enum.</param>
public sealed record GridColumn(string Key, string Type, IReadOnlyList<JsonElement>? Values);

/// <param name="TypeKey">For equipment, the model; its schema decides which attributes apply.</param>
public sealed record GridRow(string Type, long Id, string Code, string Name, string Lifecycle, ObjectRef? Site, string? TypeKey, string? Model,
    JsonElement Attributes);

public sealed record GridResult(string Kind, IReadOnlyList<GridColumn> Columns, IReadOnlyList<GridRow> Rows, bool Truncated);

public sealed class WithinRequest
{
    /// <summary>A closed or open ring of [x, y] in SWEREF 99 TM.</summary>
    public double[][] Polygon { get; set; } = [];
}

public sealed record WithinResult(IReadOnlyList<long> Sites, bool Truncated);

/// <summary>
/// The spreadsheet mode's rows (#27): the chosen sites, or the equipment on them, with their attributes. Columns come
/// from the attribute schemas of the models in the selection (and, for sites, the keys they carry). Only what the
/// caller's scopes show is included; hidden attributes are left out.
/// </summary>
public sealed class GridEndpoint(RequestDb db) : Endpoint<GridRequest, GridResult>
{
    public const int MaxSites = 5_000;
    public const int MaxRows = 5_000;

    public override void Configure() => Post("/grid");

    public override async Task HandleAsync(GridRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var sql = req.Kind == "site"
            ? $"""
                SELECT 'site', s.id, s.code, s.name, s.lifecycle::text, NULL::bigint, NULL, NULL, NULL, NULL, s.attributes::text
                FROM site s WHERE s.id = ANY($1) AND {ScopeSql.Site("s.id", 2)}
                ORDER BY s.code LIMIT {MaxRows + 1}
                """
            : $"""
                SELECT 'equipment', e.id, e.name, e.name, e.lifecycle::text, s.id, s.code, s.name, t.key, t.manufacturer || ' ' || t.model, e.attributes::text
                FROM equipment e JOIN site s ON s.id = e.site_id JOIN equipment_type t ON t.id = e.equipment_type_id
                WHERE e.site_id = ANY($1) AND {ScopeSql.Site("s.id", 2)}
                ORDER BY s.code, e.name LIMIT {MaxRows + 1}
                """;
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = req.SiteIds });
        cmd.Parameters.Add(scope.Parameter());
        var rows = new List<GridRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var attributes = JsonDocument.Parse(scope.MaskAttributes(reader.GetString(10))).RootElement.Clone();
                rows.Add(new GridRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : new ObjectRef("site", reader.GetInt64(5), reader.GetString(6), reader.GetString(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), attributes));
            }
        }
        var truncated = rows.Count > MaxRows;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        await Send.OkAsync(new GridResult(req.Kind, Columns(rows, scope), rows, truncated), ct);
    }

    /// <summary>Attribute columns: every model's schema properties, or for sites the keys the rows carry.</summary>
    internal static List<GridColumn> Columns(IReadOnlyList<GridRow> rows, UserScope scope)
    {
        var columns = new Dictionary<string, GridColumn>(StringComparer.Ordinal);
        foreach (var typeKey in rows.Where(r => r.TypeKey is not null).Select(r => r.TypeKey!).Distinct())
        {
            if (TypeCatalog.Current.Find(typeKey) is not { } type)
            {
                continue;
            }
            foreach (var field in QueryFieldsEndpoint.Attributes(type.Attributes))
            {
                columns.TryAdd(field.Key, new GridColumn(field.Key, field.Type, field.Values));
            }
        }
        foreach (var row in rows.Where(r => r.TypeKey is null))
        {
            foreach (var property in row.Attributes.EnumerateObject())
            {
                columns.TryAdd(property.Name, new GridColumn(property.Name, property.Value.ValueKind == JsonValueKind.Number ? "number" : "string", null));
            }
        }
        return [.. columns.Values.Where(c => !scope.HiddenAttributes.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal)];
    }
}

/// <summary>Sites inside a polygon drawn in the map (lasso, #27), within the caller's scopes.</summary>
public sealed class SitesWithinEndpoint(RequestDb db) : Endpoint<WithinRequest, WithinResult>
{
    public override void Configure() => Post("/sites/within");

    public override async Task HandleAsync(WithinRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (req.Polygon.Length is < 3 or > 1000 || req.Polygon.Any(p => p.Length != 2) || scope.HidesCoordinates)
        {
            AddError(r => r.Polygon, "A polygon of 3–1000 points [x, y].");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var ring = req.Polygon.Append(req.Polygon[0]).Select(p => FormattableString.Invariant($"{p[0]} {p[1]}"));
        await using var cmd = db.CreateCommand($"""
            SELECT s.id FROM site s
            WHERE ST_Intersects(s.geom, ST_MakeValid(ST_GeomFromText($1, 3006))) AND {ScopeSql.Site("s.id", 2)}
            ORDER BY s.id LIMIT {GridEndpoint.MaxSites + 1}
            """);
        cmd.Parameters.Add(new() { Value = $"POLYGON(({string.Join(", ", ring)}))" });
        cmd.Parameters.Add(scope.Parameter());
        var ids = new List<long>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetInt64(0));
            }
        }
        var truncated = ids.Count > GridEndpoint.MaxSites;
        await Send.OkAsync(new WithinResult(truncated ? ids[..GridEndpoint.MaxSites] : ids, truncated), ct);
    }
}

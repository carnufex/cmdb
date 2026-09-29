using Cmdb.Api.Auth;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cmdb.Catalog;
using FastEndpoints;
using FluentValidation;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.Api.Features.Query;

/// <summary>A test on one equipment attribute. Value is a JSON number or string, and absent for <c>exists</c>.</summary>
public sealed record AttributeCondition(string Key, string Op, JsonElement? Value);

/// <summary>
/// The site has equipment matching all the given parts (category, model, attribute), at least
/// <see cref="MinCount"/> of them (default one).
/// </summary>
public sealed record EquipmentCondition(string? Category, string? TypeKey, AttributeCondition? Attribute, int? MinCount);

public sealed record SiteQuery(
    IReadOnlyList<string>? SiteTypes,
    IReadOnlyList<string>? Lifecycles,
    IReadOnlyList<EquipmentCondition>? Equipment,
    IReadOnlyList<string>? ServiceTypes,
    int Limit = 200);

/// <param name="Matching">Equipment on the site matching the first equipment condition, if there is one.</param>
public sealed record SiteQueryHit(long Id, string Code, string Name, string SiteType, string Lifecycle, double? X, double? Y, int? Matching);

/// <param name="Points">[id, x, y] of all matches (up to 5 000), drawn as their own map layer at every zoom.</param>
/// <param name="Extent">Bounding box of those matches in SWEREF 99 TM, for "show in map".</param>
public sealed record SiteQueryResult(long Total, IReadOnlyList<SiteQueryHit> Sites, IReadOnlyList<double[]> Points, double[]? Extent, double ElapsedMs)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<long> Ids => Points.Select(p => (long)p[0]);
}

public sealed class SiteQueryValidator : Validator<SiteQuery>
{
    internal static readonly string[] Ops = ["eq", "neq", "gt", "gte", "lt", "lte", "prefix", "contains", "exists"];
    private static readonly string[] NumericOps = ["gt", "gte", "lt", "lte"];

    public SiteQueryValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 1000);
        RuleForEach(q => q.SiteTypes).Matches("^[a-z_]{1,40}$");
        RuleForEach(q => q.Lifecycles).Must(l => QueryFieldsEndpoint.Lifecycles.Contains(l)).WithMessage("Unknown lifecycle.");
        RuleForEach(q => q.ServiceTypes).Matches("^[a-z0-9_-]{1,40}$");
        RuleFor(q => q.Equipment).Must(e => e is null || e.Count <= 5).WithMessage("At most five equipment conditions.");
        RuleForEach(q => q.Equipment).ChildRules(e =>
        {
            e.RuleFor(c => c).Must(c => c.Category is not null || c.TypeKey is not null || c.Attribute is not null)
                .WithName("equipment").WithMessage("An equipment condition needs a category, a model or an attribute.");
            e.RuleFor(c => c.Category).Matches("^[a-z_]{1,40}$").When(c => c.Category is not null);
            e.RuleFor(c => c.TypeKey).Matches("^[a-z0-9-]{1,60}$").When(c => c.TypeKey is not null);
            e.RuleFor(c => c.MinCount).InclusiveBetween(1, 1000).When(c => c.MinCount is not null);
            e.RuleFor(c => c.Attribute!).ChildRules(a =>
            {
                a.RuleFor(x => x.Key).Matches("^[A-Za-z][A-Za-z0-9]{0,40}$");
                a.RuleFor(x => x.Op).Must(op => Ops.Contains(op)).WithMessage($"Operator must be one of {string.Join(", ", Ops)}.");
                a.RuleFor(x => x.Value).Must(v => v is null || v.Value.ValueKind == JsonValueKind.Undefined)
                    .When(x => x.Op == "exists").WithMessage("exists takes no value.");
                a.RuleFor(x => x.Value).Must(v => v is { ValueKind: JsonValueKind.Number })
                    .When(x => NumericOps.Contains(x.Op)).WithMessage("Comparisons need a number.");
                a.RuleFor(x => x.Value).Must(v => v is { ValueKind: JsonValueKind.String } && v.Value.GetString()!.Length is > 0 and <= 100)
                    .When(x => x.Op is "prefix" or "contains").WithMessage("Text operators need a non-empty string.");
                a.RuleFor(x => x.Value).Must(v => v is { ValueKind: JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False })
                    .When(x => x.Op is "eq" or "neq").WithMessage("Equality needs a string, number or boolean.");
            }).When(c => c.Attribute is not null);
        });
    }
}

/// <summary>
/// Advanced search (#55): sites filtered on their own fields, on the equipment they hold and on the services
/// carried through them. Every condition becomes a semi-join on site id, so Postgres can pick hash joins over
/// the whole network rather than probing per site.
/// </summary>
public sealed class QuerySitesEndpoint(RequestDb db, TypeCatalog catalog) : Endpoint<SiteQuery, SiteQueryResult>
{
    private const int MaxIds = 5_000;

    public override void Configure() => Post("/query/sites");

    public override async Task HandleAsync(SiteQuery req, CancellationToken ct)
    {
        foreach (var error in CatalogErrors(req, catalog))
        {
            AddError(error);
        }
        ThrowIfAnyErrors();

        await Send.OkAsync(await RunAsync(db, req, HttpContext.Scope(), ct), ct);
    }

    /// <summary>Runs a validated query. Also used by the MCP tool <c>find_sites</c> (#61).</summary>
    internal static async Task<SiteQueryResult> RunAsync(NpgsqlDataSource db, SiteQuery req, UserScope scope, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sql = Build(req, scope, out var parameters);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters.ToArray());

        long total = 0;
        var sites = new List<SiteQueryHit>();
        var points = new List<double[]>();
        double[]? extent = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                total = reader.GetInt64(8);
                double? x = null, y = null;
                if (!scope.HidesCoordinates)
                {
                    var (px, py) = (reader.GetDouble(5), reader.GetDouble(6));
                    extent = extent is null ? [px, py, px, py] : [Math.Min(extent[0], px), Math.Min(extent[1], py), Math.Max(extent[2], px), Math.Max(extent[3], py)];
                    points.Add([reader.GetInt64(0), Math.Round(px, 1), Math.Round(py, 1)]);
                    (x, y) = (px, py);
                }
                if (sites.Count < req.Limit)
                {
                    sites.Add(new SiteQueryHit(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), x, y, reader.IsDBNull(7) ? null : reader.GetInt32(7)));
                }
            }
        }
        return new SiteQueryResult(total, sites, points, extent, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
    }

    /// <summary>Attribute keys and models the catalog does not know. Shared with the MCP tool.</summary>
    internal static IEnumerable<string> CatalogErrors(SiteQuery req, TypeCatalog catalog)
    {
        var keys = QueryFieldsEndpoint.AttributeKeys(catalog);
        foreach (var condition in req.Equipment ?? [])
        {
            if (condition.Attribute is { } a && !keys.Contains(a.Key))
            {
                yield return $"No equipment type has the attribute '{a.Key}'.";
            }
            if (condition.TypeKey is { } key && catalog.Find(key) is null)
            {
                yield return $"Unknown equipment type '{key}'.";
            }
        }
    }

    internal static string Build(SiteQuery req, UserScope scope, out List<NpgsqlParameter> parameters)
    {
        var ps = new List<NpgsqlParameter>();
        string P(object value, NpgsqlDbType? type = null)
        {
            var p = new NpgsqlParameter { Value = value };
            if (type is { } t)
            {
                p.NpgsqlDbType = t;
            }
            ps.Add(p);
            return $"${ps.Count}";
        }

        // Only sites in the caller's scopes (#22), and only services in them count as passing a site.
        var scopeKeys = P(scope.Unrestricted ? DBNull.Value : scope.Keys, NpgsqlDbType.Array | NpgsqlDbType.Text);
        var where = new List<string> { ScopeSql.Site("s.id", int.Parse(scopeKeys[1..], CultureInfo.InvariantCulture)) };
        if (req.SiteTypes is { Count: > 0 })
        {
            where.Add($"s.site_type = ANY({P(req.SiteTypes.ToArray())}::text[])");
        }
        if (req.Lifecycles is { Count: > 0 })
        {
            where.Add($"s.lifecycle::text = ANY({P(req.Lifecycles.ToArray())}::text[])");
        }

        string? firstEquipment = null;
        foreach (var condition in req.Equipment ?? [])
        {
            var parts = new List<string>();
            if (condition.Category is { } category)
            {
                parts.Add($"et.category = {P(category)}::text");
            }
            if (condition.TypeKey is { } typeKey)
            {
                parts.Add($"et.key = {P(typeKey)}::text");
            }
            if (condition.Attribute is { } a)
            {
                parts.Add(Attribute(a, P));
            }
            var filter = string.Join(" AND ", parts);
            firstEquipment ??= filter;
            var minCount = condition.MinCount ?? 1;
            where.Add(minCount <= 1
                ? $"s.id IN (SELECT e.site_id FROM equipment e JOIN equipment_type et ON et.id = e.equipment_type_id WHERE {filter})"
                : $"s.id IN (SELECT e.site_id FROM equipment e JOIN equipment_type et ON et.id = e.equipment_type_id WHERE {filter} GROUP BY e.site_id HAVING count(*) >= {P(minCount)}::int)");
        }

        if (req.ServiceTypes is { Count: > 0 })
        {
            // A service passes a site when one of its circuits has a hop on a port of equipment there.
            where.Add($"""
                s.id IN (SELECT e.site_id FROM service v
                         JOIN service_circuit sc ON sc.service_id = v.id
                         JOIN circuit_hop h ON h.circuit_id = sc.circuit_id
                         JOIN port p ON p.terminal_id = h.terminal_id
                         JOIN equipment e ON e.id = p.equipment_id
                         WHERE v.service_type = ANY({P(req.ServiceTypes.ToArray())}::text[])
                           AND {ScopeSql.Service("v.id", int.Parse(scopeKeys[1..], CultureInfo.InvariantCulture))})
                """);
        }

        var matching = firstEquipment is null
            ? "NULL::int"
            : $"(SELECT count(*) FROM equipment e JOIN equipment_type et ON et.id = e.equipment_type_id WHERE e.site_id = m.id AND {firstEquipment})::int";

        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"""
            WITH m AS (
                SELECT s.id, s.code, s.name, s.site_type, s.lifecycle::text AS lifecycle,
                       ST_X(ST_PointOnSurface(s.geom)) AS x, ST_Y(ST_PointOnSurface(s.geom)) AS y
                FROM site s
                {(where.Count > 0 ? "WHERE " + string.Join("\n  AND ", where) : "")}
            ), counted AS (SELECT count(*) AS total FROM m)
            SELECT m.id, m.code, m.name, m.site_type, m.lifecycle, m.x, m.y,
                   CASE WHEN row_number() OVER (ORDER BY m.code) <= {req.Limit} THEN {matching} END,
                   counted.total
            FROM m, counted
            ORDER BY m.code
            LIMIT {MaxIds}
            """);
        parameters = ps;
        return sql.ToString();
    }

    private static string Attribute(AttributeCondition a, Func<object, NpgsqlDbType?, string> p)
    {
        var key = p(a.Key, NpgsqlDbType.Text);
        var value = a.Value ?? default;
        string Json() => p(JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [a.Key] = value }), NpgsqlDbType.Jsonb);
        string Number() => p(value.GetDecimal(), NpgsqlDbType.Numeric);
        string Text() => p(value.GetString()!, NpgsqlDbType.Text);
        string Numeric(string op) => $"(jsonb_typeof(e.attributes -> {key}) = 'number' AND (e.attributes ->> {key})::numeric {op} {Number()})";
        return a.Op switch
        {
            // Containment uses the jsonb_path_ops GIN index.
            "eq" => $"e.attributes @> {Json()}",
            "neq" => $"(e.attributes ? {key} AND NOT e.attributes @> {Json()})",
            "gt" => Numeric(">"),
            "gte" => Numeric(">="),
            "lt" => Numeric("<"),
            "lte" => Numeric("<="),
            "prefix" => $"starts_with(e.attributes ->> {key}, {Text()})",
            "contains" => $"strpos(lower(e.attributes ->> {key}), lower({Text()})) > 0",
            "exists" => $"e.attributes ? {key}",
            _ => throw new ArgumentOutOfRangeException(nameof(a), a.Op, "Unknown operator."),
        };
    }
}

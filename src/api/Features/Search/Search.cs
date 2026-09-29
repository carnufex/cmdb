using Cmdb.Api.Auth;
using System.Globalization;
using FastEndpoints;
using FluentValidation;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.Api.Features.Search;

public sealed class SearchRequest
{
    [QueryParam]
    public string Q { get; set; } = "";

    /// <summary>Optional "x,y" in SWEREF 99 TM, usually the map centre. Nearer objects rank higher.</summary>
    [QueryParam]
    public string? Near { get; set; }

    [QueryParam]
    public int Limit { get; set; } = 20;
}

/// <summary>A hit. X and Y (SWEREF 99 TM) are set for objects with a place, so the map can centre on them.</summary>
public sealed record SearchHit(string Type, long Id, string Code, string? Name, string? Detail, string Lifecycle, double? X, double? Y);

public sealed class SearchValidator : Validator<SearchRequest>
{
    public SearchValidator()
    {
        // Substring search needs a trigram; shorter queries only make sense as ids.
        RuleFor(r => r.Q).NotEmpty().MaximumLength(100)
            .Must(q => q.Trim().Length >= 3 || long.TryParse(q.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .WithMessage("Search needs at least three characters, or a numeric id.");
        RuleFor(r => r.Limit).InclusiveBetween(1, 50);
        RuleFor(r => r.Near).Must(n => n is null || TryParsePoint(n, out _, out _)).WithMessage("near is 'x,y'.");
    }

    internal static bool TryParsePoint(string value, out double x, out double y)
    {
        x = y = 0;
        var parts = value.Split(',');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }
}

/// <summary>
/// Quick search over sites, equipment, cables, services and circuits (pg_trgm, see #12). Ranking: exact code or
/// id, then prefix, then contains; within a level by object type, distance to <c>near</c> and similarity.
/// </summary>
public sealed class SearchEndpoint(NpgsqlDataSource db) : Endpoint<SearchRequest, IReadOnlyList<SearchHit>>
{
    private const int PerLevel = 40;

    /// <summary>One searchable object type: how to select a hit and which columns match at each level.</summary>
    /// <param name="Scope">Keeps the source inside the caller's scopes (#22); $11 is the scope keys.</param>
    private sealed record Source(string Type, int Rank, string Select, string From, string Exact, string Prefix, string Contains, string Scope);

    private static readonly Source[] Sources =
    [
        new("site", 0,
            // The first source names the columns of the union.
            "s.id AS id, s.code AS code, s.name AS name, s.site_type AS detail, s.lifecycle::text AS lifecycle, ST_X(ST_PointOnSurface(s.geom)) AS x, ST_Y(ST_PointOnSurface(s.geom)) AS y, greatest(similarity(s.code, $1::text), similarity(s.name, $1::text)) AS sim",
            "site s",
            "s.code = $2::text OR s.id = $10::bigint",
            "s.code LIKE $4::text OR lower(s.name) LIKE $5::text",
            "s.code ILIKE $6::text OR s.name ILIKE $6::text",
            ScopeSql.Site("s.id", 11)),
        new("equipment", 1,
            "e.id, e.name, NULL, et.manufacturer || ' ' || et.model, e.lifecycle::text, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)), similarity(e.name, $1::text)",
            "equipment e JOIN equipment_type et ON et.id = e.equipment_type_id JOIN site s ON s.id = e.site_id",
            "lower(e.name) = $3::text OR e.id = $10::bigint",
            "lower(e.name) LIKE $5::text",
            "e.name ILIKE $6::text OR e.attributes::text ILIKE $6::text",
            ScopeSql.Site("s.id", 11)),
        new("cable", 2,
            "c.id, c.code, ct.name, ct.medium::text, c.lifecycle::text, ST_X(ST_LineInterpolatePoint(c.geom, 0.5)), ST_Y(ST_LineInterpolatePoint(c.geom, 0.5)), similarity(c.code, $1::text)",
            "cable c JOIN cable_type ct ON ct.id = c.cable_type_id",
            "c.code = $2::text OR c.id = $10::bigint",
            "c.code LIKE $4::text",
            "c.code ILIKE $6::text",
            ScopeSql.Cable("c.id", 11)),
        new("service", 3,
            "v.id, v.code, v.name, v.service_type, v.lifecycle::text, NULL::float8, NULL::float8, greatest(similarity(v.code, $1::text), similarity(v.name, $1::text))",
            "service v",
            "v.code = $2::text OR v.id = $10::bigint",
            "v.code LIKE $4::text OR lower(v.name) LIKE $5::text",
            "v.code ILIKE $6::text OR v.name ILIKE $6::text",
            ScopeSql.Service("v.id", 11)),
        new("circuit", 4,
            "r.id, r.code, NULL, r.layer::text, r.lifecycle::text, NULL::float8, NULL::float8, similarity(r.code, $1::text)",
            "circuit r",
            "r.code = $2::text OR r.id = $10::bigint",
            "r.code LIKE $4::text",
            "r.code ILIKE $6::text",
            ScopeSql.Circuit("r.id", 11)),
    ];

    // Every level is a LIMIT without ORDER BY, so Postgres stops reading as soon as it has enough candidates
    // even for a term that matches tens of thousands of rows. Only the few candidates are ranked.
    // Exact and prefix levels use B-tree pattern indexes (codes are upper case, names via lower(name));
    // trigram indexes serve only the contains level, where they are the right tool.
    private static readonly string Sql = $"""
        WITH candidates AS (
        {string.Join("\n    UNION ALL\n", Sources.SelectMany(s => new[] { (0, s.Exact), (1, s.Prefix), (2, s.Contains) }.Select(level =>
            $"    (SELECT '{s.Type}' AS type, {level.Item1} AS match_rank, {s.Rank} AS type_rank, {s.Select} FROM {s.From} WHERE ({level.Item2}) AND {s.Scope} LIMIT {PerLevel})")))}
        ), hits AS (
            SELECT DISTINCT ON (type, id) * FROM candidates ORDER BY type, id, match_rank
        )
        SELECT h.type, h.id, h.code, h.name, h.detail, h.lifecycle, h.x, h.y
        FROM hits h
        ORDER BY h.match_rank, h.type_rank,
                 CASE WHEN $7::float8 IS NULL OR h.x IS NULL THEN 0 ELSE (h.x - $7::float8) ^ 2 + (h.y - $8::float8) ^ 2 END,
                 h.sim DESC, h.code
        LIMIT $9
        """;

    public override void Configure() => Get("/search");

    public override async Task HandleAsync(SearchRequest req, CancellationToken ct)
    {
        var q = req.Q.Trim();
        double? nx = null, ny = null;
        if (req.Near is not null && SearchValidator.TryParsePoint(req.Near, out var x, out var y))
        {
            (nx, ny) = (x, y);
        }
        await Send.OkAsync(await RunAsync(db, q, nx, ny, req.Limit, HttpContext.Scope(), ct), ct);
    }

    /// <summary>Also used by the MCP tool <c>search</c> (#61).</summary>
    internal static async Task<List<SearchHit>> RunAsync(NpgsqlDataSource db, string q, double? nx, double? ny, int limit, UserScope scope, CancellationToken ct)
    {
        var literal = EscapeLike(q);

        // Values are passed as plain parameters so each execution is planned with them; a LIKE 'prefix%' can
        // then use the B-tree pattern indexes.
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Trigram selectivity estimates are poor: for a term with few or no matches the planner expects thousands and
        // bets on a sequential or full index scan filling LIMIT early, then reads the whole table (200 ms for a miss at
        // full scale). Bitmap scans build the match set from the index first and stop reading rows at LIMIT, so only
        // those are allowed. Scoped to this transaction; see #12 for the measurements.
        await using (var setting = new NpgsqlCommand("SET LOCAL enable_seqscan = off; SET LOCAL enable_indexscan = off", conn, tx))
        {
            await setting.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = q });
        cmd.Parameters.Add(new NpgsqlParameter { Value = q.ToUpperInvariant() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = q.ToLowerInvariant() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = $"{literal.ToUpperInvariant()}%" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = $"{literal.ToLowerInvariant()}%" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = q.Length >= 3 ? $"%{literal}%" : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)nx ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)ny ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
        // An id match only when the query is a number, so the primary key index is used.
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = long.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Bigint,
        });
        cmd.Parameters.Add(scope.Parameter());

        var hits = new List<SearchHit>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            hits.Add(new SearchHit(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7)));
        }
        return hits;
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
             .Replace("%", @"\%", StringComparison.Ordinal)
             .Replace("_", @"\_", StringComparison.Ordinal);

    /// <summary>The generated SQL, for the benchmark script and for reading.</summary>
    internal static string GeneratedSql => Sql;
}

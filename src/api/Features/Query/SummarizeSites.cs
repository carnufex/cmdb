using System.Globalization;
using Cmdb.Api.Auth;
using Cmdb.Catalog;
using FastEndpoints;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.Api.Features.Query;

public sealed record SiteSummaryRequest(SiteQuery Query, string? GroupBy = null, string? SumAttribute = null);

public sealed record SiteSummaryGroup(string Key, long Sites, decimal? Sum);

/// <param name="Sum">The summed attribute over all matches; null when none was asked for.</param>
public sealed record SiteSummary(long Total, decimal? Sum, IReadOnlyList<SiteSummaryGroup> Groups, double ElapsedMs);

/// <summary>How many sites match, and what their equipment adds up to (#334). Same conditions as advanced search, under the same scope.</summary>
public sealed class SummarizeSitesEndpoint(RequestDb db, TypeCatalog catalog) : Endpoint<SiteSummaryRequest, SiteSummary>
{
    private static readonly Dictionary<string, string> Groupings = new(StringComparer.Ordinal)
    {
        ["siteType"] = "s.site_type",
        ["lifecycle"] = "s.lifecycle::text",
        ["region"] = QuerySitesEndpoint.RegionSql,
    };

    public override void Configure() => Post("/query/sites/summary");

    public override async Task HandleAsync(SiteSummaryRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        foreach (var error in new SiteQueryValidator().Validate(req.Query).Errors.Select(e => e.ErrorMessage)
            .Concat(QuerySitesEndpoint.CatalogErrors(req.Query, catalog, scope)).Concat(Errors(req, catalog, scope)))
        {
            AddError(error);
        }
        ThrowIfAnyErrors();
        await Send.OkAsync(await RunAsync(db, req, scope, ct), ct);
    }

    internal static IEnumerable<string> Errors(SiteSummaryRequest req, TypeCatalog catalog, UserScope scope)
    {
        if (req.GroupBy is { } group && !Groupings.ContainsKey(group))
        {
            yield return $"groupBy must be one of {string.Join(", ", Groupings.Keys)}.";
        }
        // Northing per group says where a site lies, which the scope hides.
        if (req.GroupBy == "region" && scope.HidesCoordinates)
        {
            yield return "Sites cannot be grouped by region, since your access hides their positions.";
        }
        if (req.SumAttribute is { } key && (!QueryFieldsEndpoint.AttributeKeys(catalog).Contains(key) || scope.HiddenAttributes.Contains(key)))
        {
            yield return $"No equipment type has the attribute '{key}'.";
        }
    }

    internal static async Task<SiteSummary> RunAsync(NpgsqlDataSource db, SiteSummaryRequest req, UserScope scope, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var ps = new List<NpgsqlParameter>();
        var where = QuerySitesEndpoint.Conditions(req.Query, scope, ps, out _);
        var group = req.GroupBy is { } g ? Groupings[g] : "''";
        var sum = "NULL::numeric";
        var join = "";
        if (req.SumAttribute is { } key)
        {
            ps.Add(new NpgsqlParameter { Value = key, NpgsqlDbType = NpgsqlDbType.Text });
            var p = string.Create(CultureInfo.InvariantCulture, $"${ps.Count}");
            sum = "sum(d.amount)";
            join = $"""
                LEFT JOIN (SELECT e.site_id, sum((e.attributes ->> {p})::numeric) AS amount
                           FROM equipment e WHERE jsonb_typeof(e.attributes -> {p}) = 'number' GROUP BY e.site_id) d ON d.site_id = m.id
                """;
        }
        var sql = $"""
            WITH m AS (SELECT s.id, {group} AS key FROM site s WHERE {string.Join(" AND ", where)})
            SELECT m.key, count(*), {sum}
            FROM m {join}
            GROUP BY m.key
            ORDER BY count(*) DESC, m.key
            """;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(ps.ToArray());
        var groups = new List<SiteSummaryGroup>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            groups.Add(new SiteSummaryGroup(reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetDecimal(2)));
        }
        return new SiteSummary(groups.Sum(x => x.Sites), req.SumAttribute is null ? null : groups.Sum(x => x.Sum ?? 0),
            req.GroupBy is null ? [] : groups, Math.Round(started.Elapsed.TotalMilliseconds, 1));
    }
}

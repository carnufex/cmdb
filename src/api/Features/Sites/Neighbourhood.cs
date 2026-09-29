using Cmdb.Api.Auth;
using System.ComponentModel;
using Cmdb.Api.Agents;
using FastEndpoints;
using FluentValidation;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.Sites;

public sealed class NeighbourhoodRequest
{
    public long Id { get; set; }

    [QueryParam]
    public int Hops { get; set; } = 1;
}

/// <param name="Via">The cable to the nearest neighbour on the way, for sites one hop away.</param>
public sealed record Neighbour(long Id, string Code, string Name, string SiteType, string Lifecycle, int Hops, string? Via);

public sealed class NeighbourhoodValidator : Validator<NeighbourhoodRequest>
{
    public NeighbourhoodValidator() => RuleFor(r => r.Hops).InclusiveBetween(1, 3);
}

/// <summary>Sites reachable from a site over cables, up to three hops, nearest first.</summary>
public sealed class NeighbourhoodEndpoint(RequestDb db) : Endpoint<NeighbourhoodRequest, IReadOnlyList<Neighbour>>
{
    public const int Max = 200;

    // Access scopes (#22): the walk only crosses cables in scope and stops at sites outside it, which are not listed.
    private static readonly string Sql = $"""
        WITH RECURSIVE walk(site_id, hops, via) AS (
            SELECT $1::bigint, 0, NULL::text WHERE {ScopeSql.Site("$1::bigint", 4)}
            UNION ALL
            SELECT CASE WHEN c.a_site_id = w.site_id THEN c.b_site_id ELSE c.a_site_id END, w.hops + 1,
                   CASE WHEN w.hops = 0 THEN c.code ELSE w.via END
            FROM walk w JOIN cable c ON c.a_site_id = w.site_id OR c.b_site_id = w.site_id
            WHERE w.hops < $2 AND {ScopeSql.Cable("c.id", 4)}
              AND {ScopeSql.Site("CASE WHEN c.a_site_id = w.site_id THEN c.b_site_id ELSE c.a_site_id END", 4)}
        ), nearest AS (
            SELECT DISTINCT ON (site_id) site_id, hops, via FROM walk WHERE site_id <> $1 ORDER BY site_id, hops, via
        )
        SELECT s.id, s.code, s.name, s.site_type, s.lifecycle::text, n.hops, CASE WHEN n.hops = 1 THEN n.via END
        FROM nearest n JOIN site s ON s.id = n.site_id
        ORDER BY n.hops, s.code
        LIMIT $3
        """;

    public override void Configure() => Get("/sites/{id}/neighbourhood");

    public override async Task HandleAsync(NeighbourhoodRequest req, CancellationToken ct) =>
        await Send.OkAsync(await RunAsync(db, req.Id, req.Hops, Max, HttpContext.Scope(), ct), ct);

    internal static async Task<List<Neighbour>> RunAsync(NpgsqlDataSource db, long id, int hops, int limit, UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(Sql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = hops });
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
        cmd.Parameters.Add(scope.Parameter());
        var neighbours = new List<Neighbour>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            neighbours.Add(new Neighbour(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return neighbours;
    }
}

public sealed record AgentNeighbour(string Ref, string Code, string Name, string SiteType, string Lifecycle, int Hops, string? ViaCable, string Url);

public sealed record NeighbourhoodResult(string Ref, IReadOnlyList<AgentNeighbour> Sites, bool Truncated);

[McpServerToolType]
public sealed class NeighbourhoodTools(RequestDb db, AgentLinks links, IHttpContextAccessor http)
{
    private const int MaxSites = 50;

    [McpServerTool(Name = "neighbourhood", Title = "Grannskap", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Sites connected to a site by cables, up to three hops away, nearest first. Direct neighbours name the cable between them.")]
    public async Task<NeighbourhoodResult> Neighbourhood(
        [Description("Site id, as in \"site:1268\".")] long siteId,
        [Description("How many cable hops to follow, 1–3.")] int hops = 1,
        CancellationToken ct = default)
    {
        if (hops is < 1 or > 3)
        {
            throw new McpException("hops must be between 1 and 3.");
        }
        var sites = await NeighbourhoodEndpoint.RunAsync(db, siteId, hops, MaxSites + 1, http.HttpContext!.Scope(), ct);
        return new NeighbourhoodResult(
            AgentLinks.Ref("site", siteId),
            [.. sites.Take(MaxSites).Select(s => new AgentNeighbour(AgentLinks.Ref("site", s.Id), s.Code, s.Name, s.SiteType, s.Lifecycle, s.Hops, s.Via, links.For("site", s.Id)))],
            sites.Count > MaxSites);
    }
}

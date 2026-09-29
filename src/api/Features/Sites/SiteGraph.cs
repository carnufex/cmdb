using Cmdb.Api.Auth;
using System.Diagnostics;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Sites;

public sealed record SiteGraphRequest(long Id);

public sealed record SiteGraphNode(long Id, string Code, string Name, string SiteType, string Lifecycle, double X, double Y);

/// <param name="Id">Stable across expansions: "cable:{id}" for a cable, "{layer}:{lower site}-{higher site}" for circuits.</param>
/// <param name="Layer">physical (a cable), transmission or logical (circuits between the two sites).</param>
/// <param name="Circuits">For circuit edges, how many circuits run between the sites in that layer.</param>
public sealed record SiteGraphEdge(string Id, long Source, long Target, string Layer, long? CableId, string? Code, string? Lifecycle, int Circuits);

/// <param name="Truncated">More neighbours than <see cref="SiteGraphEndpoint.Max"/>; the nearest by cable come first.</param>
public sealed record SiteGraph(SiteGraphNode Site, IReadOnlyList<SiteGraphNode> Nodes, IReadOnlyList<SiteGraphEdge> Edges, bool Truncated, double ElapsedMs);

/// <summary>
/// One level of the neighbourhood graph (#20): the sites next to a site by cable, and by transmission and logical
/// circuits ending at it (aggregated per site and layer). The lens expands one level at a time by asking again.
/// </summary>
public sealed class SiteGraphEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks) : Endpoint<SiteGraphRequest, SiteGraph>
{
    public const int Max = 500;

    public override void Configure() => Get("/sites/{id}/graph");

    public override async Task HandleAsync(SiteGraphRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var mask = holder.Current is { } g ? await masks.GetAsync(g, scope, ct) : null;
        var result = await RunAsync(holder.Current, mask, db, req.Id, scope, ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(result, ct);
    }

    /// <remarks>Access scopes (#22): only cables, circuits and sites in scope; a site outside it is not found.</remarks>
    internal static async Task<SiteGraph?> RunAsync(Cmdb.Graph.Graph? g, GraphMask? mask, NpgsqlDataSource db, long siteId, UserScope scope, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var edges = new List<SiteGraphEdge>();
        await using (var cmd = db.CreateCommand($"""
            SELECT c.id, c.code, c.lifecycle::text, c.a_site_id, c.b_site_id
            FROM cable c
            WHERE (c.a_site_id = $1 OR c.b_site_id = $1) AND c.a_site_id <> c.b_site_id
              AND {ScopeSql.Cable("c.id", 2)}
              AND {ScopeSql.Site("c.a_site_id", 2)} AND {ScopeSql.Site("c.b_site_id", 2)}
            ORDER BY c.code
            """))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = siteId });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                edges.Add(new SiteGraphEdge($"cable:{reader.GetInt64(0)}", reader.GetInt64(3), reader.GetInt64(4), "physical",
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), 0));
            }
        }

        // Circuits above the physical layer that end at the site, counted per far site and layer, from the graph.
        if (g is not null && mask is not null && g.TryGetSite(siteId, out var site) && mask.Sites[site])
        {
            var counts = new Dictionary<(long Other, CircuitLayer Layer), int>();
            var seen = new HashSet<int>();
            foreach (var equipment in g.EquipmentAt(site))
            {
                foreach (var port in g.PortsOf(equipment))
                {
                    foreach (var circuit in g.CircuitsThrough(port))
                    {
                        var layer = g.LayerOf(circuit);
                        if (layer == CircuitLayer.Physical || !seen.Add(circuit) || !mask.CircuitVisible(circuit))
                        {
                            continue;
                        }
                        var hops = g.HopsOf(circuit);
                        var a = g.SiteOf(hops[0]);
                        var b = g.SiteOf(hops[^1]);
                        var other = a == siteId ? b : a;
                        if (other is { } o && o != siteId && (a == siteId || b == siteId)
                            && g.TryGetSite(o, out var otherIndex) && mask.Sites[otherIndex])
                        {
                            counts[(o, layer)] = counts.GetValueOrDefault((o, layer)) + 1;
                        }
                    }
                }
            }
            foreach (var ((other, layer), count) in counts.OrderBy(c => c.Key.Other).ThenBy(c => c.Key.Layer))
            {
                var name = layer == CircuitLayer.Logical ? "logical" : "transmission";
                var (lo, hi) = siteId < other ? (siteId, other) : (other, siteId);
                edges.Add(new SiteGraphEdge($"{name}:{lo}-{hi}", siteId, other, name, null, null, null, count));
            }
        }

        // The nearest neighbours by cable first, then by circuits, capped.
        var neighbourIds = edges.Select(e => e.Source == siteId ? e.Target : e.Source).Distinct().ToList();
        var truncated = neighbourIds.Count > Max;
        neighbourIds = [.. neighbourIds.Take(Max)];
        var keep = neighbourIds.ToHashSet();
        edges = [.. edges.Where(e => keep.Contains(e.Source == siteId ? e.Target : e.Source))];

        var nodes = new Dictionary<long, SiteGraphNode>();
        await using (var cmd = db.CreateCommand($"""
            SELECT id, code, name, site_type, lifecycle::text, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom))
            FROM site WHERE id = ANY($1) AND {ScopeSql.Site("site.id", 2)}
            """))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = neighbourIds.Append(siteId).ToArray() });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                nodes[reader.GetInt64(0)] = new SiteGraphNode(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetDouble(5), reader.GetDouble(6));
            }
        }
        if (!nodes.TryGetValue(siteId, out var self))
        {
            return null;
        }
        return new SiteGraph(self, [.. neighbourIds.Where(nodes.ContainsKey).Select(id => nodes[id])], edges, truncated,
            Math.Round(sw.Elapsed.TotalMilliseconds, 2));
    }
}

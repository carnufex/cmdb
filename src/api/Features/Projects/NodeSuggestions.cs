using System.Diagnostics;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using FastEndpoints;
using FluentValidation;
using Npgsql;

namespace Cmdb.Api.Features.Projects;

public sealed class NodeSuggestionsRequest
{
    public long Id { get; set; }

    /// <summary>The most homes and premises one node serves.</summary>
    [QueryParam]
    public int Capacity { get; set; } = 64;
}

public sealed class NodeSuggestionsValidator : Validator<NodeSuggestionsRequest>
{
    public NodeSuggestionsValidator() => RuleFor(r => r.Capacity).InclusiveBetween(8, 1024);
}

/// <summary>A suggested node (#294): where it would stand, what it was moved to, and the service points it would serve.</summary>
/// <param name="SnappedTo">An existing site where conduit meets (a manhole, say) the node was moved to; null when it is on the grid.</param>
/// <param name="ReachM">The farthest of its service points, in metres.</param>
/// <param name="OverCapacity">A single point with more units than a node takes: it gets a node of its own anyway.</param>
public sealed record NodeSuggestion(int Number, double X, double Y, ObjectRef? SnappedTo, int ServicePoints, int Units, double ReachM, bool OverCapacity,
    IReadOnlyList<long> ServicePointIds);

public sealed record NodeSuggestions(int Capacity, int ServicePoints, int Units, IReadOnlyList<NodeSuggestion> Nodes, double ElapsedMs);

/// <summary>
/// Where to put distribution nodes in a project's area (#294): its unconnected service points in clusters of at most
/// <c>capacity</c> units, each node at its cluster's centre moved to the nearest site where conduit meets within 150 m,
/// or to a 25 m grid. Nothing is written; the web puts a suggestion in a plan as a cabinet from a template.
/// </summary>
public sealed class NodeSuggestionsEndpoint(RequestDb db, ProjectService projects) : Endpoint<NodeSuggestionsRequest, NodeSuggestions>
{
    public const double SnapM = 150;
    public const double GridM = 25;

    public override void Configure() => Get("/projects/{id}/node-suggestions");

    public override async Task HandleAsync(NodeSuggestionsRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (await projects.GetAsync(req.Id, scope, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (scope.HidesCoordinates)
        {
            // Positions are what the suggestions are made of.
            await Send.OkAsync(new NodeSuggestions(req.Capacity, 0, 0, [], 0), ct);
            return;
        }
        var sw = Stopwatch.StartNew();
        var points = new List<NodeClustering.Item>();
        await using (var cmd = db.CreateCommand($"""
            SELECT v.id, ST_X(v.geom), ST_Y(v.geom), v.units FROM service_point v JOIN project p ON p.id = $1
            WHERE ST_Covers(p.area, v.geom) AND v.site_id IS NULL AND {ScopeSql.ServicePoint("v.geom", 2)}
            ORDER BY v.id
            """))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                points.Add(new NodeClustering.Item(reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3)));
            }
        }
        var clusters = NodeClustering.Cluster(points, req.Capacity);
        var nodes = new List<NodeSuggestion>();
        foreach (var (cluster, i) in clusters.Select((c, i) => (c, i)))
        {
            var units = cluster.Sum(p => p.Units);
            var (cx, cy) = (cluster.Sum(p => p.X * p.Units) / units, cluster.Sum(p => p.Y * p.Units) / units);
            var snapped = await SnapAsync(cx, cy, scope, ct);
            var (x, y) = snapped is { } s ? (s.X, s.Y) : (Math.Round(cx / GridM) * GridM, Math.Round(cy / GridM) * GridM);
            nodes.Add(new NodeSuggestion(i + 1, Math.Round(x, 1), Math.Round(y, 1), snapped?.Site, cluster.Count, units,
                Math.Round(cluster.Max(p => Math.Sqrt(((p.X - x) * (p.X - x)) + ((p.Y - y) * (p.Y - y)))), 0),
                cluster.Count == 1 && units > req.Capacity, [.. cluster.Select(p => p.Id)]));
        }
        await Send.OkAsync(new NodeSuggestions(req.Capacity, points.Count, points.Sum(p => p.Units), nodes, Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ct);
    }

    /// <summary>The nearest site within reach where a route segment ends, in the caller's scopes.</summary>
    private async Task<(ObjectRef Site, double X, double Y)?> SnapAsync(double x, double y, UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT s.id, s.code, s.name, s.lifecycle::text, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom))
            FROM site s
            WHERE ST_DWithin(s.geom, ST_SetSRID(ST_MakePoint($1, $2), 3006), {SnapM.ToString(System.Globalization.CultureInfo.InvariantCulture)})
              AND EXISTS (SELECT 1 FROM route_segment r WHERE r.a_site_id = s.id OR r.b_site_id = s.id)
              AND {ScopeSql.Site("s.id", 3)}
            ORDER BY s.geom <-> ST_SetSRID(ST_MakePoint($1, $2), 3006) LIMIT 1
            """);
        cmd.Parameters.Add(new() { Value = x });
        cmd.Parameters.Add(new() { Value = y });
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (new ObjectRef("site", reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)), reader.GetDouble(4), reader.GetDouble(5))
            : null;
    }
}

/// <summary>
/// Clusters service points for nodes (#294): k-means weighted by units with k-means++ seeding from a fixed seed, so the
/// same points give the same clusters; then any cluster over the capacity is split in two the same way until all fit.
/// </summary>
public static class NodeClustering
{
    public sealed record Item(long Id, double X, double Y, int Units);

    public static List<List<Item>> Cluster(IReadOnlyList<Item> items, int capacity)
    {
        if (items.Count == 0)
        {
            return [];
        }
        var k = Math.Max(1, (int)Math.Ceiling(items.Sum(p => p.Units) / (double)capacity));
        var pending = new Queue<List<Item>>(KMeans(items, Math.Min(k, items.Count)));
        var done = new List<List<Item>>();
        while (pending.Count > 0)
        {
            var cluster = pending.Dequeue();
            if (cluster.Sum(p => p.Units) <= capacity || cluster.Count == 1)
            {
                done.Add(cluster);
                continue;
            }
            foreach (var part in KMeans(cluster, 2))
            {
                pending.Enqueue(part);
            }
        }
        // West to east, then south to north, so the numbering reads across the map.
        return [.. done.OrderBy(c => Math.Round(c.Average(p => p.X) / 200)).ThenBy(c => c.Average(p => p.Y))];
    }

    private static List<List<Item>> KMeans(IReadOnlyList<Item> items, int k)
    {
        var rng = new Random(294);
        var centres = new List<(double X, double Y)> { (items[0].X, items[0].Y) };
        // k-means++: each next centre far from those chosen, weighted by distance squared and units.
        while (centres.Count < k)
        {
            var weights = items.Select(p => centres.Min(c => Sq(p.X - c.X) + Sq(p.Y - c.Y)) * p.Units).ToArray();
            var total = weights.Sum();
            if (total <= 0)
            {
                break;
            }
            var pick = rng.NextDouble() * total;
            var i = 0;
            for (; i < weights.Length - 1 && pick > weights[i]; i++)
            {
                pick -= weights[i];
            }
            centres.Add((items[i].X, items[i].Y));
        }
        var assignment = new int[items.Count];
        for (var round = 0; round < 50; round++)
        {
            var changed = false;
            for (var i = 0; i < items.Count; i++)
            {
                var best = 0;
                var bestD = double.MaxValue;
                for (var c = 0; c < centres.Count; c++)
                {
                    var d = Sq(items[i].X - centres[c].X) + Sq(items[i].Y - centres[c].Y);
                    if (d < bestD)
                    {
                        (best, bestD) = (c, d);
                    }
                }
                changed |= assignment[i] != best;
                assignment[i] = best;
            }
            for (var c = 0; c < centres.Count; c++)
            {
                var members = items.Where((_, i) => assignment[i] == c).ToList();
                if (members.Count > 0)
                {
                    var units = members.Sum(p => p.Units);
                    centres[c] = (members.Sum(p => p.X * p.Units) / units, members.Sum(p => p.Y * p.Units) / units);
                }
            }
            if (!changed && round > 0)
            {
                break;
            }
        }
        return [.. Enumerable.Range(0, centres.Count).Select(c => items.Where((_, i) => assignment[i] == c).ToList()).Where(m => m.Count > 0)];
    }

    private static double Sq(double v) => v * v;
}

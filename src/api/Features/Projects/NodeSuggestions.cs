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
        var units = clusters.Select(c => c.Sum(p => p.Units)).ToList();
        var centres = clusters.Select((c, i) => (X: c.Sum(p => p.X * p.Units) / units[i], Y: c.Sum(p => p.Y * p.Units) / units[i])).ToList();
        var sites = await SnapAsync(centres, scope, ct);
        var nodes = new List<NodeSuggestion>();
        foreach (var (cluster, i) in clusters.Select((c, i) => (c, i)))
        {
            var (cx, cy) = centres[i];
            var snapped = sites[i];
            var (x, y) = snapped is { } s ? (s.X, s.Y) : (Math.Round(cx / GridM) * GridM, Math.Round(cy / GridM) * GridM);
            nodes.Add(new NodeSuggestion(i + 1, Math.Round(x, 1), Math.Round(y, 1), snapped?.Site, cluster.Count, units[i],
                Math.Round(cluster.Max(p => Math.Sqrt(((p.X - x) * (p.X - x)) + ((p.Y - y) * (p.Y - y)))), 0),
                cluster.Count == 1 && units[i] > req.Capacity, [.. cluster.Select(p => p.Id)]));
        }
        await Send.OkAsync(new NodeSuggestions(req.Capacity, points.Count, points.Sum(p => p.Units), nodes, Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ct);
    }

    /// <summary>
    /// For each centre, the nearest site within reach where a route segment ends, in the caller's scopes. One query for
    /// all of them: a round trip per node was a third of the time (#325).
    /// </summary>
    private async Task<(ObjectRef Site, double X, double Y)?[]> SnapAsync(List<(double X, double Y)> centres, UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT c.i, n.id, n.code, n.name, n.lifecycle, n.x, n.y
            FROM unnest($1::float8[], $2::float8[]) WITH ORDINALITY c(x, y, i)
            CROSS JOIN LATERAL (
                SELECT s.id, s.code, s.name, s.lifecycle::text AS lifecycle, ST_X(ST_PointOnSurface(s.geom)) AS x, ST_Y(ST_PointOnSurface(s.geom)) AS y
                FROM site s
                WHERE ST_DWithin(s.geom, ST_SetSRID(ST_MakePoint(c.x, c.y), 3006), {SnapM.ToString(System.Globalization.CultureInfo.InvariantCulture)})
                  AND EXISTS (SELECT 1 FROM route_segment r WHERE r.a_site_id = s.id OR r.b_site_id = s.id)
                  AND {ScopeSql.Site("s.id", 3)}
                ORDER BY s.geom <-> ST_SetSRID(ST_MakePoint(c.x, c.y), 3006) LIMIT 1
            ) n
            """);
        cmd.Parameters.Add(new() { Value = centres.Select(c => c.X).ToArray() });
        cmd.Parameters.Add(new() { Value = centres.Select(c => c.Y).ToArray() });
        cmd.Parameters.Add(scope.Parameter());
        var sites = new (ObjectRef Site, double X, double Y)?[centres.Count];
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sites[(int)reader.GetInt64(0) - 1] = (new ObjectRef("site", reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), reader.GetDouble(5), reader.GetDouble(6));
        }
        return sites;
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
        var n = items.Count;
        var (xs, ys, unitsOf) = (new double[n], new double[n], new double[n]);
        for (var i = 0; i < n; i++)
        {
            (xs[i], ys[i], unitsOf[i]) = (items[i].X, items[i].Y, items[i].Units);
        }
        var (cx, cy) = (new double[k], new double[k]);
        (cx[0], cy[0]) = (xs[0], ys[0]);
        var count = 1;
        // k-means++: each next centre far from those chosen, weighted by distance squared and units.
        var nearest = new double[n];
        Array.Fill(nearest, double.MaxValue);
        var weights = new double[n];
        while (count < k)
        {
            // Only the newest centre can be nearer than before.
            var (lastX, lastY) = (cx[count - 1], cy[count - 1]);
            var total = 0d;
            for (var i = 0; i < n; i++)
            {
                nearest[i] = Math.Min(nearest[i], Sq(xs[i] - lastX) + Sq(ys[i] - lastY));
                weights[i] = nearest[i] * unitsOf[i];
                total += weights[i];
            }
            if (total <= 0)
            {
                break;
            }
            var pick = rng.NextDouble() * total;
            var chosen = 0;
            for (; chosen < n - 1 && pick > weights[chosen]; chosen++)
            {
                pick -= weights[chosen];
            }
            (cx[count], cy[count]) = (xs[chosen], ys[chosen]);
            count++;
        }
        var assignment = new int[n];
        var (sumX, sumY, sumUnits) = (new double[count], new double[count], new double[count]);
        for (var round = 0; round < 50; round++)
        {
            var changed = false;
            Array.Clear(sumX);
            Array.Clear(sumY);
            Array.Clear(sumUnits);
            for (var i = 0; i < n; i++)
            {
                var best = 0;
                var bestD = double.MaxValue;
                for (var c = 0; c < count; c++)
                {
                    var d = Sq(xs[i] - cx[c]) + Sq(ys[i] - cy[c]);
                    if (d < bestD)
                    {
                        (best, bestD) = (c, d);
                    }
                }
                changed |= assignment[i] != best;
                assignment[i] = best;
                sumX[best] += xs[i] * unitsOf[i];
                sumY[best] += ys[i] * unitsOf[i];
                sumUnits[best] += unitsOf[i];
            }
            for (var c = 0; c < count; c++)
            {
                if (sumUnits[c] > 0)
                {
                    (cx[c], cy[c]) = (sumX[c] / sumUnits[c], sumY[c] / sumUnits[c]);
                }
            }
            if (!changed && round > 0)
            {
                break;
            }
        }
        var members = new List<Item>[count];
        for (var c = 0; c < count; c++)
        {
            members[c] = [];
        }
        for (var i = 0; i < n; i++)
        {
            members[assignment[i]].Add(items[i]);
        }
        return [.. members.Where(m => m.Count > 0)];
    }

    private static double Sq(double v) => v * v;
}

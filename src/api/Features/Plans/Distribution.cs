using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Projects;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;
using FluentValidation;

namespace Cmdb.Api.Features.Plans;

public sealed class DistributionRequest
{
    public long Id { get; set; }

    /// <summary>The node the cables start from: an existing site, or one the plan creates (negative id).</summary>
    public long Node { get; set; }

    /// <summary>The service points the node serves, e.g. a node suggestion's (#294).</summary>
    public long[] ServicePoints { get; set; } = [];

    /// <summary>Spare fibres on top of the units downstream, as a share: 0.2 is 20 %.</summary>
    public double Reserve { get; set; } = 0.2;

    /// <summary>The most units one distribution point (splice closure) serves.</summary>
    public int GroupSize { get; set; } = 12;

    /// <summary>Work it out without adding anything to the plan.</summary>
    public bool DryRun { get; set; } = true;
}

public sealed class DistributionValidator : Validator<DistributionRequest>
{
    public DistributionValidator()
    {
        RuleFor(r => r.Node).NotEqual(0);
        RuleFor(r => r.ServicePoints).Must(p => p is { Length: >= 1 and <= 5000 }).WithMessage("servicePoints is 1–5000 service points.");
        RuleFor(r => r.Reserve).InclusiveBetween(0, 2);
        RuleFor(r => r.GroupSize).InclusiveBetween(2, 96);
    }
}

/// <summary>A distribution point (#295): a splice closure where a group of service points will be served.</summary>
public sealed record DistributionPoint(int Number, string Code, double X, double Y, int ServicePoints, int Units);

/// <summary>
/// A suggested cable from the node or a distribution point to a distribution point, sized for the units downstream plus the
/// reserve, rounded up to the smallest fibre cable type that fits.
/// </summary>
/// <param name="From">0 for the node, otherwise a distribution point's number.</param>
/// <param name="InConduitM">Metres along existing route segments; the rest is new, on the grid.</param>
/// <param name="Way">How it is drawn (#259): route segments to follow and break points, ending at the distribution point.</param>
public sealed record DistributionCable(int From, int To, int Units, int Fibres, string TypeKey, string TypeName, int Conductors, double LengthM,
    double InConduitM, IReadOnlyList<DistributionStep> Way);

/// <summary>A step of a suggested cable's way: a route segment (<c>segment</c>, by id), a site the way passes (<c>site</c>), or a break point.</summary>
public sealed record DistributionStep(string Kind, long? Id = null, double? X = null, double? Y = null);

/// <param name="Operations">With dryRun false: the operations added to the plan.</param>
public sealed record DistributionPlan(IReadOnlyList<DistributionPoint> Points, IReadOnlyList<DistributionCable> Cables, double LengthM, int Units,
    IReadOnlyList<string> Warnings, int Operations, double ElapsedMs);

/// <summary>
/// Distribution cables from a node to its service points (#295, #260): the points in groups for distribution points (splice
/// closures), a tree from the node over them (the shortest on a grid, since there is no road network to follow yet), and each
/// edge a cable sized for the units it carries plus the reserve, rounded up to the catalog's smallest fibre cable that fits.
/// Added to the plan as splice points and drawn cables (#259), so each can be redrawn.
/// </summary>
public sealed class Distribution(RequestDb db, GraphHolder holder, PlanViews views, PlanPatterns patterns)
{
    public async Task<PlanWrite<DistributionPlan>> SuggestAsync(ClaimsPrincipal user, UserScope scope, DistributionRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (await views.GetAsync(holder.Require(), req.Id, scope, ct) is not { } view)
        {
            return PlanWrite.Fail<DistributionPlan>(PlanWriteFailure.NotFound, $"Plan {req.Id} finns inte.");
        }
        if (!req.DryRun && view.Chain.Plan.Status != "draft")
        {
            return PlanWrite.Fail<DistributionPlan>(PlanWriteFailure.Conflict, "Bara utkast kan ändras.");
        }
        // The node: a site the plan creates, or one in production in the caller's scopes.
        (double X, double Y, string Code)? node = null;
        if (req.Node < 0)
        {
            if (view.Chain.Operations.FirstOrDefault(o => o.Kind == "create_site" && Planned.ObjectId(o.Id) == req.Node) is { } created)
            {
                node = (created.Payload.GetProperty("x").GetDouble(), created.Payload.GetProperty("y").GetDouble(), created.Payload.GetProperty("code").GetString()!);
            }
        }
        else if (await PlanSql.ObjectVisibleAsync(db, "site", req.Node, scope, ct))
        {
            await using var cmd = db.CreateCommand("SELECT ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)), code FROM site WHERE id = $1");
            cmd.Parameters.Add(new() { Value = req.Node });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                node = (reader.GetDouble(0), reader.GetDouble(1), reader.GetString(2));
            }
        }
        if (node is not { } n)
        {
            return PlanWrite.Fail<DistributionPlan>(PlanWriteFailure.Invalid, $"Noden {req.Node} finns inte.");
        }

        var items = new List<NodeClustering.Item>();
        await using (var cmd = db.CreateCommand($"""
            SELECT v.id, ST_X(v.geom), ST_Y(v.geom), v.units FROM service_point v WHERE v.id = ANY($1) AND {ScopeSql.ServicePoint("v.geom", 2)} ORDER BY v.id
            """))
        {
            cmd.Parameters.Add(new() { Value = req.ServicePoints });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new NodeClustering.Item(reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3)));
            }
        }
        if (items.Count != req.ServicePoints.Distinct().Count())
        {
            return PlanWrite.Fail<DistributionPlan>(PlanWriteFailure.Invalid, "En av anslutningspunkterna finns inte.");
        }

        var warnings = new List<string>();
        var prefix = n.Code;
        var groups = NodeClustering.Cluster(items, req.GroupSize);
        var points = groups.Select((g, i) =>
        {
            var units = g.Sum(p => p.Units);
            return new DistributionPoint(i + 1, string.Create(CultureInfo.InvariantCulture, $"{prefix}-F{i + 1:00}"),
                Math.Round(g.Sum(p => p.X * p.Units) / units / 5) * 5, Math.Round(g.Sum(p => p.Y * p.Units) / units / 5) * 5, g.Count, units);
        }).ToList();
        var conduit = await ConduitAsync(n.X, n.Y, points, scope, ct);
        var cables = Tree(n.X, n.Y, req.Node > 0 ? req.Node : null, points, req.Reserve, warnings, conduit);
        var result = new DistributionPlan(points, cables, Math.Round(cables.Sum(c => c.LengthM), 0), points.Sum(p => p.Units), warnings, 0,
            Math.Round(sw.Elapsed.TotalMilliseconds, 1));
        if (req.DryRun)
        {
            return new(result);
        }

        // Splice points first: the cables refer to them by the ids their operations give them.
        var spliceType = TypeCatalog.Current.SiteTypesWith(CatalogRoles.SplicePoint).FirstOrDefault()
            ?? TypeCatalog.Current.SiteTypes[0].Key;
        var sites = await patterns.BatchAsync(user, scope, req.Id, [.. points.Select(p => new AddOperationRequest
        {
            Kind = "create_site", Code = p.Code, Name = $"Fördelningspunkt {p.Number} ({n.Code})", SiteType = spliceType, X = p.X, Y = p.Y,
        })], ct);
        if (sites.Value is null)
        {
            return PlanWrite.Fail<DistributionPlan>(sites.Failure, sites.Error!);
        }
        long Site(int number) => number == 0 ? req.Node : sites.Value[number - 1].Target!.Id;
        var added = await patterns.BatchAsync(user, scope, req.Id, [.. cables.Select(c => new AddOperationRequest
        {
            Kind = "create_cable", ASiteId = Site(c.From), BSiteId = Site(c.To), TypeKey = c.TypeKey,
            Route = [.. c.Way.Select(w => new CableRouteStep { Kind = w.Kind, Id = w.Id, X = w.X, Y = w.Y }),
                new CableRouteStep { Kind = "site", Id = Site(c.To) }],
        })], ct);
        if (added.Value is null)
        {
            await patterns.RemoveAsync(req.Id, [.. sites.Value.Select(s => s.Id)], ct);
            return PlanWrite.Fail<DistributionPlan>(added.Failure, added.Error!);
        }
        return new(result with { Operations = sites.Value.Count + added.Value.Count, ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1) });
    }

    /// <summary>
    /// The tree from the node: Prim's algorithm on grid (Manhattan) distances, each edge a cable from the vertex nearer the
    /// node, sized for the units in the subtree beyond it.
    /// </summary>
    /// <summary>Existing route segments around the node and its distribution points, as a graph over the sites they join.</summary>
    internal sealed record Conduit(Dictionary<long, (double X, double Y)> Sites, Dictionary<long, List<(long Segment, long To, double LengthM)>> Edges)
    {
        public static readonly Conduit None = new([], []);

        /// <summary>The conduit site nearest a point within <paramref name="reachM"/>, or the given site when it is one.</summary>
        public long? Near(double x, double y, long? site, double reachM = 60)
        {
            if (site is { } s && Edges.ContainsKey(s))
            {
                return s;
            }
            var best = Sites.Where(k => Edges.ContainsKey(k.Key))
                .Select(k => (k.Key, D: Math.Sqrt(((k.Value.X - x) * (k.Value.X - x)) + ((k.Value.Y - y) * (k.Value.Y - y)))))
                .Where(k => k.D <= reachM).OrderBy(k => k.D).FirstOrDefault();
            return best.D > 0 || best.Key != 0 ? best.Key : null;
        }

        /// <summary>The shortest way over the segments, as segment ids in order and its length; null when there is none.</summary>
        public (List<long> Segments, double LengthM)? Path(long from, long to)
        {
            if (from == to)
            {
                return ([], 0);
            }
            var dist = new Dictionary<long, double> { [from] = 0 };
            var via = new Dictionary<long, (long Site, long Segment)>();
            var queue = new PriorityQueue<long, double>();
            queue.Enqueue(from, 0);
            while (queue.TryDequeue(out var v, out var d))
            {
                if (v == to)
                {
                    break;
                }
                if (d > dist[v])
                {
                    continue;
                }
                foreach (var (segment, next, length) in Edges.GetValueOrDefault(v) ?? [])
                {
                    var nd = d + length;
                    if (nd < dist.GetValueOrDefault(next, double.MaxValue))
                    {
                        dist[next] = nd;
                        via[next] = (v, segment);
                        queue.Enqueue(next, nd);
                    }
                }
            }
            if (!dist.TryGetValue(to, out var total))
            {
                return null;
            }
            var segments = new List<long>();
            for (var at = to; at != from; at = via[at].Site)
            {
                segments.Add(via[at].Segment);
            }
            segments.Reverse();
            return (segments, total);
        }
    }

    private async Task<Conduit> ConduitAsync(double nx, double ny, IReadOnlyList<DistributionPoint> points, UserScope scope, CancellationToken ct)
    {
        var xs = points.Select(p => p.X).Append(nx).ToArray();
        var ys = points.Select(p => p.Y).Append(ny).ToArray();
        await using var cmd = db.CreateCommand($"""
            SELECT r.id, r.a_site_id, r.b_site_id, r.length_m,
                   ST_X(ST_PointOnSurface(a.geom)), ST_Y(ST_PointOnSurface(a.geom)), ST_X(ST_PointOnSurface(b.geom)), ST_Y(ST_PointOnSurface(b.geom))
            FROM route_segment r JOIN site a ON a.id = r.a_site_id JOIN site b ON b.id = r.b_site_id
            WHERE r.geom && ST_MakeEnvelope($1, $2, $3, $4, 3006) AND r.lifecycle <> 'removed' AND {ScopeSql.RouteSegment("r.id", 5)}
            """);
        cmd.Parameters.Add(new() { Value = xs.Min() - 2000 });
        cmd.Parameters.Add(new() { Value = ys.Min() - 2000 });
        cmd.Parameters.Add(new() { Value = xs.Max() + 2000 });
        cmd.Parameters.Add(new() { Value = ys.Max() + 2000 });
        cmd.Parameters.Add(scope.Parameter());
        var conduit = new Conduit([], []);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var (segment, a, b, length) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3));
            conduit.Sites[a] = (reader.GetDouble(4), reader.GetDouble(5));
            conduit.Sites[b] = (reader.GetDouble(6), reader.GetDouble(7));
            (conduit.Edges.TryGetValue(a, out var fromA) ? fromA : conduit.Edges[a] = []).Add((segment, b, length));
            (conduit.Edges.TryGetValue(b, out var fromB) ? fromB : conduit.Edges[b] = []).Add((segment, a, length));
        }
        return conduit;
    }

    internal static List<DistributionCable> Tree(double nx, double ny, long? nodeSite, IReadOnlyList<DistributionPoint> points, double reserve,
        List<string> warnings, Conduit? conduit = null)
    {
        conduit ??= Conduit.None;
        var count = points.Count;
        var parent = new int[count + 1];
        var best = Enumerable.Repeat(double.MaxValue, count + 1).ToArray();
        var inTree = new bool[count + 1];
        (double X, double Y) At(int v) => v == 0 ? (nx, ny) : (points[v - 1].X, points[v - 1].Y);
        double Grid(int a, int b) => Math.Abs(At(a).X - At(b).X) + Math.Abs(At(a).Y - At(b).Y);
        best[0] = 0;
        for (var step = 0; step <= count; step++)
        {
            var v = -1;
            for (var i = 0; i <= count; i++)
            {
                if (!inTree[i] && (v < 0 || best[i] < best[v]))
                {
                    v = i;
                }
            }
            inTree[v] = true;
            for (var i = 0; i <= count; i++)
            {
                if (!inTree[i] && Grid(v, i) < best[i])
                {
                    (best[i], parent[i]) = (Grid(v, i), v);
                }
            }
        }
        // Units beyond each vertex: its own and its children's, from the leaves up.
        var units = new int[count + 1];
        var children = Enumerable.Range(0, count + 1).Select(_ => new List<int>()).ToArray();
        for (var i = 1; i <= count; i++)
        {
            children[parent[i]].Add(i);
        }
        int Sum(int v) => units[v] = (v == 0 ? 0 : points[v - 1].Units) + children[v].Sum(Sum);
        Sum(0);

        var fibre = TypeCatalog.Current.CableTypes.Where(t => t.Medium == "fiber").OrderBy(t => t.ConductorCount).ThenBy(t => t.CostPerMeter ?? 0).ToList();
        var cables = new List<DistributionCable>();
        for (var i = 1; i <= count; i++)
        {
            var needed = (int)Math.Ceiling(units[i] * (1 + reserve));
            var type = fibre.FirstOrDefault(t => t.ConductorCount >= needed) ?? fibre[^1];
            if (type.ConductorCount < needed)
            {
                warnings.Add($"Kabeln till fördelningspunkt {i} behöver {needed} fibrer; den största kabeltypen har {type.ConductorCount}.");
            }
            var (a, b) = (At(parent[i]), At(i));
            var (way, length, inConduit) = Way(a, b, parent[i] == 0 ? nodeSite : null, conduit, Grid(parent[i], i));
            cables.Add(new DistributionCable(parent[i], i, units[i], needed, type.Key, type.Name, type.ConductorCount, Math.Round(length, 0),
                Math.Round(inConduit, 0), way));
        }
        return [.. cables.OrderBy(c => c.From).ThenBy(c => c.To)];
    }

    /// <summary>
    /// A cable's way from a to b: along existing route segments when both ends have a conduit site near them and the way over
    /// the segments is at most half again as long as the grid, otherwise on the grid (along x, then y). New stretches to and
    /// from the conduit are dug. The last step, to the distribution point itself, is added by the caller.
    /// </summary>
    private static (List<DistributionStep> Way, double LengthM, double InConduitM) Way((double X, double Y) a, (double X, double Y) b, long? aSite,
        Conduit conduit, double grid)
    {
        if (conduit.Near(a.X, a.Y, aSite) is { } entry && conduit.Near(b.X, b.Y, null) is { } exit && entry != exit
            && conduit.Path(entry, exit) is { } path && path.LengthM <= grid * 1.5)
        {
            var e = conduit.Sites[entry];
            var x = conduit.Sites[exit];
            var way = new List<DistributionStep>();
            var toEntry = Math.Abs(e.X - a.X) + Math.Abs(e.Y - a.Y);
            if (entry != aSite)
            {
                way.Add(new DistributionStep("site", entry));
            }
            way.AddRange(path.Segments.Select(id => new DistributionStep("segment", id)));
            var fromExit = Math.Abs(x.X - b.X) + Math.Abs(x.Y - b.Y);
            return (way, (entry == aSite ? 0 : toEntry) + path.LengthM + fromExit, path.LengthM);
        }
        // On the grid: along x first, then y; straight when they share an axis.
        return (Math.Abs(a.X - b.X) < 1 || Math.Abs(a.Y - b.Y) < 1 ? [] : [new DistributionStep("point", null, b.X, a.Y)], grid, 0);
    }
}

/// <summary>
/// POST a node's distribution (#295): a dry run shows it to anyone who sees the plan, adding it to the plan takes
/// <c>cmdb-full</c> like every other plan write.
/// </summary>
public sealed class DistributionEndpoint(Distribution distribution) : Endpoint<DistributionRequest, DistributionPlan>
{
    public override void Configure() => Post("/plans/{id}/distribution");

    public override async Task HandleAsync(DistributionRequest req, CancellationToken ct)
    {
        if (!req.DryRun && !User.IsInRole("cmdb-full"))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var result = await distribution.SuggestAsync(User, HttpContext.Scope(), req, ct);
        switch (result.Failure)
        {
            case PlanWriteFailure.None:
                await Send.OkAsync(result.Value!, ct);
                return;
            case PlanWriteFailure.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanWriteFailure.Conflict:
                await PlanSql.ConflictAsync(HttpContext, result.Error!, ct);
                return;
            default:
                await Send.ResultAsync(TypedResults.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest));
                return;
        }
    }
}

using System.Diagnostics;
using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

public sealed record RouteSite(long Id, string Code, string Name);

/// <param name="Free">Conductors free along their whole length: no splice, patch or termination at either end.</param>
public sealed record RouteCable(long Id, string Code, RouteSite From, RouteSite To, double LengthM, int Free);

/// <param name="StraightM">Distance in a straight line; the real route comes with the ducts (#92).</param>
public sealed record RouteNewCable(RouteSite A, RouteSite B, string TypeKey, double StraightM);

/// <param name="Cables">The existing cables in order from the start.</param>
/// <param name="NewCableAfter">How many of the cables come before the new cable, when there is one.</param>
/// <param name="LengthM">Existing cable length plus the straight line of a new cable.</param>
/// <param name="Splices">Where the fibres are joined through: one per site between two cables.</param>
public sealed record RouteAlternative(IReadOnlyList<RouteSite> Sites, IReadOnlyList<RouteCable> Cables, RouteNewCable? NewCable, int NewCableAfter,
    double LengthM, int Splices);

public sealed record RouteSuggestion(RouteSite From, RouteSite To, int Fibres, IReadOnlyList<RouteAlternative> Alternatives, string? Note,
    double ElapsedMs);

public sealed class RouteRequest
{
    /// <summary>The start site: its code, or its id.</summary>
    public string From { get; set; } = "";

    public string To { get; set; } = "";

    /// <summary>How many fibres the connection needs, 1–96.</summary>
    public int Fibres { get; set; } = 1;

    /// <summary>Count the plan's view instead of production: what earlier changes in the plan already use is not free.</summary>
    public long? Plan { get; set; }
}

public sealed class AddRouteRequest
{
    public long Id { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Fibres { get; set; } = 1;

    /// <summary>Which of the suggested alternatives, from 0.</summary>
    public int Alternative { get; set; }
}

/// <summary>
/// Route suggestion for a new connection between two sites (#171): the shortest ways over existing cables that still
/// have free fibres, weighted on length, on how many joints they need and on how much of a cable's spare fibres they would
/// use, and, when there is no way, a new cable between the closest sites of the two sides. Added to a plan, a route
/// becomes splices through each site in between and, where needed, a new cable. Calculated on cables and sites: the exact
/// ducts come with the conduit model (#92).
/// </summary>
public sealed class RoutePlanner(RequestDb db, GraphHolder holder, PlanViews views, PlanPatterns patterns)
{
    public const int MaxAlternatives = 3;
    private const double SplicePenaltyM = 300;
    private const double MaxNewCableM = 100_000;
    private const double CellM = 10_000;

    private sealed record Edge(long Id, string Code, long A, long B, double LengthM, int Free)
    {
        public long Other(long site) => site == A ? B : A;
    }

    private sealed class Search(Dictionary<long, double> dist, Dictionary<long, Edge> via, long source)
    {
        public IReadOnlyDictionary<long, double> Dist => dist;

        /// <summary>The cables from the source to the site, in order; null when it cannot be reached.</summary>
        public List<Edge>? Path(long target)
        {
            if (!dist.ContainsKey(target))
            {
                return null;
            }
            var path = new List<Edge>();
            for (var at = target; at != source;)
            {
                var edge = via[at];
                path.Add(edge);
                at = edge.Other(at);
            }
            path.Reverse();
            return path;
        }
    }

    public async Task<PlanWrite<RouteSuggestion>> SuggestAsync(UserScope scope, RouteRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (req.Fibres is < 1 or > 96)
        {
            return PlanWrite.Fail<RouteSuggestion>(PlanWriteFailure.Invalid, "Antalet fibrer är 1–96.");
        }
        var graph = holder.Require();
        if (req.Plan is { } planId)
        {
            if (await views.GetAsync(graph, planId, scope, ct) is not { } view)
            {
                return PlanWrite.Fail<RouteSuggestion>(PlanWriteFailure.NotFound, $"Plan {planId} finns inte.");
            }
            graph = view.Graph;
        }
        if (await SiteAsync(scope, req.From, ct) is not { } from)
        {
            return PlanWrite.Fail<RouteSuggestion>(PlanWriteFailure.NotFound, $"Siten {req.From} finns inte.");
        }
        if (await SiteAsync(scope, req.To, ct) is not { } to)
        {
            return PlanWrite.Fail<RouteSuggestion>(PlanWriteFailure.NotFound, $"Siten {req.To} finns inte.");
        }
        if (from.Id == to.Id)
        {
            return PlanWrite.Fail<RouteSuggestion>(PlanWriteFailure.Invalid, "Start och mål är samma site.");
        }

        var adjacency = new Dictionary<long, List<Edge>>();
        foreach (var e in await EdgesAsync(scope, graph, req.Fibres, ct))
        {
            foreach (var site in new[] { e.A, e.B })
            {
                if (!adjacency.TryGetValue(site, out var list))
                {
                    adjacency[site] = list = [];
                }
                list.Add(e);
            }
        }

        // Alternatives: the best way, then the best without the longest cable of the one before.
        var banned = new HashSet<long>();
        var found = new List<List<Edge>>();
        for (var i = 0; i < MaxAlternatives * 2 && found.Count < MaxAlternatives; i++)
        {
            var path = Shortest(adjacency, from.Id, banned, req.Fibres).Path(to.Id);
            if (path is null)
            {
                break;
            }
            if (!found.Any(f => f.Select(e => e.Id).SequenceEqual(path.Select(e => e.Id))))
            {
                found.Add(path);
            }
            banned.Add(path.OrderByDescending(e => e.LengthM).First().Id);
        }

        var sites = new Dictionary<long, RouteSite> { [from.Id] = from, [to.Id] = to };
        string? note = null;
        var alternatives = new List<RouteAlternative>();
        foreach (var path in found)
        {
            alternatives.Add(await ToAlternativeAsync(scope, sites, from.Id, path, null, [], ct));
        }
        if (found.Count == 0)
        {
            var (a, b, straight) = await ClosestAsync(scope, adjacency, from.Id, to.Id, req.Fibres, ct);
            if (a is null || b is null)
            {
                note = $"Ingen väg med {req.Fibres} lediga fibrer, och ingen site på andra sidan inom {MaxNewCableM / 1000:0} km att dra en ny kabel till.";
            }
            else if (CableTypeFor(req.Fibres) is not { } type)
            {
                note = $"Ingen fiberkabel i katalogen har {req.Fibres} fibrer.";
            }
            else
            {
                var head = Shortest(adjacency, from.Id, [], req.Fibres).Path(a.Value) ?? [];
                var tail = Shortest(adjacency, to.Id, [], req.Fibres).Path(b.Value) ?? [];
                tail.Reverse();
                alternatives.Add(await ToAlternativeAsync(scope, sites, from.Id, head, (a.Value, b.Value, type, straight), tail, ct));
                note = $"Ingen väg med {req.Fibres} lediga fibrer över befintliga kablar. En ny kabel behövs mellan "
                    + $"{sites[a.Value].Code} och {sites[b.Value].Code} ({straight / 1000:0.0} km i fågelvägen).";
            }
        }
        return new(new RouteSuggestion(from, to, req.Fibres, alternatives, note, Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
    }

    /// <summary>
    /// Adds an alternative to the plan: a new cable if it has one, then splices of the free fibres through each site in
    /// between. All or nothing.
    /// </summary>
    public async Task<PlanWrite<List<PlanOperationView>>> AddAsync(ClaimsPrincipal user, UserScope scope, AddRouteRequest req, CancellationToken ct)
    {
        var suggestion = await SuggestAsync(scope, new RouteRequest { From = req.From, To = req.To, Fibres = req.Fibres, Plan = req.Id }, ct);
        if (suggestion.Value is not { } s)
        {
            return PlanWrite.Fail<List<PlanOperationView>>(suggestion.Failure, suggestion.Error!);
        }
        if (req.Alternative < 0 || req.Alternative >= s.Alternatives.Count)
        {
            return PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.Invalid,
                s.Alternatives.Count == 0 ? s.Note ?? "Ingen väg att lägga till." : $"Alternativet finns inte; välj 0–{s.Alternatives.Count - 1}.");
        }
        var route = s.Alternatives[req.Alternative];
        if (await views.GetAsync(holder.Require(), req.Id, scope, ct) is not { } view)
        {
            return PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.NotFound, $"Plan {req.Id} finns inte.");
        }
        if (view.Chain.Plan.Status != "draft")
        {
            return PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.Conflict, "Bara utkast kan ändras.");
        }

        var added = new List<PlanOperationView>();
        async Task<PlanWrite<List<PlanOperationView>>> UndoAsync(PlanWrite<List<PlanOperationView>> failure)
        {
            await patterns.RemoveAsync(req.Id, [.. added.Select(a => a.Id)], ct);
            return failure;
        }

        // The cables in order of travel, each as (cable, the site it is entered at, the site it leaves at, its A site).
        var steps = new List<(long Cable, long Enter, long Leave, long ASite)>();
        var at = s.From.Id;
        void Walk(IEnumerable<RouteCable> cables)
        {
            foreach (var c in cables)
            {
                var next = c.From.Id == at ? c.To.Id : c.From.Id;
                steps.Add((c.Id, at, next, c.From.Id));
                at = next;
            }
        }
        Walk(route.Cables.Take(route.NewCableAfter));
        if (route.NewCable is { } nc)
        {
            var made = await patterns.BatchAsync(user, scope, req.Id, [new AddOperationRequest
            {
                Kind = "create_cable", ASiteId = nc.A.Id, BSiteId = nc.B.Id, TypeKey = nc.TypeKey,
            }], ct);
            if (made.Value is null)
            {
                return made;
            }
            added.AddRange(made.Value);
            steps.Add((made.Value[0].Target!.Id, nc.A.Id, nc.B.Id, nc.A.Id));
            at = nc.B.Id;
        }
        Walk(route.Cables.Skip(route.NewCableAfter));

        var splices = new List<AddOperationRequest>();
        for (var i = 0; i + 1 < steps.Count; i++)
        {
            var (first, second) = (steps[i], steps[i + 1]);
            var a = await FreeEndsAsync(scope, view.Graph, req.Id, first.Cable, first.ASite == first.Leave ? "A" : "B", ct);
            var b = await FreeEndsAsync(scope, view.Graph, req.Id, second.Cable, second.ASite == second.Enter ? "A" : "B", ct);
            if (a.Count < req.Fibres || b.Count < req.Fibres)
            {
                return await UndoAsync(PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.Conflict,
                    $"Vid {route.Sites[i + 1].Code} finns inte {req.Fibres} lediga fibrer på båda kablarna. Inget lades till."));
            }
            splices.AddRange(a.Zip(b).Take(req.Fibres)
                .Select(p => new AddOperationRequest { Kind = "connect", A = p.First, B = p.Second, ConnectionKind = "splice" }));
        }
        if (splices.Count > 0)
        {
            var result = await patterns.BatchAsync(user, scope, req.Id, splices, ct);
            if (result.Value is null)
            {
                return await UndoAsync(result);
            }
            added.AddRange(result.Value);
        }
        return new(added);
    }

    /// <summary>The terminals at one side of a cable whose conductor is free along its whole length, by conductor number.</summary>
    private async Task<List<long>> FreeEndsAsync(UserScope scope, Cmdb.Graph.Graph graph, long planId, long cable, string side, CancellationToken ct)
    {
        var here = await patterns.ConductorEndsAsync(planId, cable, side, scope, ct);
        var there = (await patterns.ConductorEndsAsync(planId, cable, side == "A" ? "B" : "A", scope, ct)).ToDictionary(t => t.Number);
        return [.. here.Where(t => FreeTerminal(graph, t.Terminal) && there.TryGetValue(t.Number, out var other) && FreeTerminal(graph, other.Terminal))
            .Select(t => t.Terminal)];
    }

    private static bool FreeTerminal(Cmdb.Graph.Graph graph, long terminal) => !graph.TryGetNode(terminal, out var node) || FreeNode(graph, node);

    private static bool FreeNode(Cmdb.Graph.Graph graph, int node)
    {
        foreach (var kind in graph.NeighbourKinds(node))
        {
            if (kind != EdgeKind.Conductor)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The cables that can carry the fibres: in service, in the caller's scopes and with enough free conductors.</summary>
    private async Task<List<Edge>> EdgesAsync(UserScope scope, Cmdb.Graph.Graph graph, int fibres, CancellationToken ct)
    {
        var edges = new List<Edge>();
        await using var cmd = db.Source.CreateCommand($"""
            SELECT c.id, c.code, c.a_site_id, c.b_site_id, c.length_m FROM cable c
            WHERE c.lifecycle = 'in_service' AND c.valid_to IS NULL AND {ScopeSql.Cable("c.id", 1)}
            """);
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!graph.TryGetCable(reader.GetInt64(0), out var cable))
            {
                continue;
            }
            var free = FreeConductors(graph, cable);
            if (free >= fibres)
            {
                edges.Add(new Edge(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetDouble(4), free));
            }
        }
        return edges;
    }

    /// <summary>Conductors with nothing spliced, patched or terminated at either end.</summary>
    private static int FreeConductors(Cmdb.Graph.Graph graph, int cable)
    {
        var count = 0;
        foreach (var end in graph.EndsOf(cable))
        {
            if (!FreeNode(graph, end))
            {
                continue;
            }
            // The other end of the conductor is the neighbour across its own edge.
            var kinds = graph.NeighbourKinds(end);
            var neighbours = graph.Neighbours(end);
            for (var i = 0; i < kinds.Length; i++)
            {
                if (kinds[i] == EdgeKind.Conductor && FreeNode(graph, neighbours[i]))
                {
                    count++;
                }
            }
        }
        return count / 2;
    }

    private static Search Shortest(Dictionary<long, List<Edge>> adjacency, long source, HashSet<long> banned, int fibres)
    {
        var dist = new Dictionary<long, double> { [source] = 0 };
        var via = new Dictionary<long, Edge>();
        var done = new HashSet<long>();
        var queue = new PriorityQueue<long, double>();
        queue.Enqueue(source, 0);
        while (queue.TryDequeue(out var site, out var d))
        {
            if (!done.Add(site) || !adjacency.TryGetValue(site, out var list))
            {
                continue;
            }
            foreach (var e in list.Where(e => !banned.Contains(e.Id)))
            {
                // Length, a penalty for each joint, and a little more the larger a share of the spare fibres it takes.
                var next = d + e.LengthM + SplicePenaltyM + (e.LengthM * 0.2 * fibres / e.Free);
                var other = e.Other(site);
                if (!dist.TryGetValue(other, out var known) || next < known)
                {
                    dist[other] = next;
                    via[other] = e;
                    queue.Enqueue(other, next);
                }
            }
        }
        return new Search(dist, via, source);
    }

    /// <summary>The closest pair of sites between what the start reaches and what the goal reaches, for a new cable between them.</summary>
    private async Task<(long? A, long? B, double Straight)> ClosestAsync(UserScope scope, Dictionary<long, List<Edge>> adjacency, long from, long to,
        int fibres, CancellationToken ct)
    {
        var left = Shortest(adjacency, from, [], fibres).Dist.Keys.ToArray();
        var right = Shortest(adjacency, to, [], fibres).Dist.Keys.ToArray();
        var points = new Dictionary<long, (double X, double Y)>();
        await using (var cmd = db.Source.CreateCommand($"""
            SELECT s.id, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)) FROM site s
            WHERE s.id = ANY($1) AND {ScopeSql.Site("s.id", 2)}
            """))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = left.Concat(right).ToArray() });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                points[reader.GetInt64(0)] = (reader.GetDouble(1), reader.GetDouble(2));
            }
        }
        // The goal's side in a grid, so each site on the start's side looks only at its own neighbourhood.
        var grid = new Dictionary<(int, int), List<long>>();
        foreach (var id in right.Where(points.ContainsKey))
        {
            var (x, y) = points[id];
            var cell = ((int)Math.Floor(x / CellM), (int)Math.Floor(y / CellM));
            if (!grid.TryGetValue(cell, out var list))
            {
                grid[cell] = list = [];
            }
            list.Add(id);
        }
        (long? A, long? B, double Straight) best = (null, null, MaxNewCableM);
        var reach = (int)Math.Ceiling(MaxNewCableM / CellM);
        foreach (var a in left.Where(points.ContainsKey))
        {
            var (ax, ay) = points[a];
            var (cx, cy) = ((int)Math.Floor(ax / CellM), (int)Math.Floor(ay / CellM));
            for (var dx = -reach; dx <= reach; dx++)
            {
                for (var dy = -reach; dy <= reach; dy++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy), out var cell))
                    {
                        continue;
                    }
                    foreach (var b in cell)
                    {
                        var d = Math.Sqrt(Math.Pow(points[b].X - ax, 2) + Math.Pow(points[b].Y - ay, 2));
                        if (d < best.Straight)
                        {
                            best = (a, b, d);
                        }
                    }
                }
            }
        }
        return best;
    }

    private static string? CableTypeFor(int fibres) => TypeCatalog.Embedded.CableTypes
        .Where(t => t.Medium.Equals("fiber", StringComparison.OrdinalIgnoreCase) && t.ConductorCount >= fibres)
        .OrderBy(t => t.ConductorCount).ThenBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;

    private async Task<RouteAlternative> ToAlternativeAsync(UserScope scope, Dictionary<long, RouteSite> sites, long start, List<Edge> head,
        (long A, long B, string Type, double Straight)? newCable, List<Edge> tail, CancellationToken ct)
    {
        var all = head.Concat(tail).ToList();
        var ids = all.SelectMany(e => new[] { e.A, e.B }).Concat(newCable is { } n ? [n.A, n.B] : []).Distinct().Where(id => !sites.ContainsKey(id)).ToArray();
        if (ids.Length > 0)
        {
            await using var cmd = db.Source.CreateCommand($"SELECT s.id, s.code, s.name FROM site s WHERE s.id = ANY($1) AND {ScopeSql.Site("s.id", 2)}");
            cmd.Parameters.Add(new NpgsqlParameter { Value = ids });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sites[reader.GetInt64(0)] = new RouteSite(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
            }
        }
        RouteSite Site(long id) => sites.TryGetValue(id, out var s) ? s : new RouteSite(id, $"site:{id}", "");
        var order = new List<RouteSite> { Site(start) };
        var at = start;
        void Walk(IEnumerable<Edge> edges)
        {
            foreach (var e in edges)
            {
                at = e.Other(at);
                order.Add(Site(at));
            }
        }
        Walk(head);
        if (newCable is { } nc)
        {
            order.Add(Site(nc.B));
            at = nc.B;
        }
        Walk(tail);
        var cables = all.Select(e => new RouteCable(e.Id, e.Code, Site(e.A), Site(e.B), Math.Round(e.LengthM), e.Free)).ToList();
        var newOne = newCable is { } c ? new RouteNewCable(Site(c.A), Site(c.B), c.Type, Math.Round(c.Straight)) : null;
        return new RouteAlternative(order, cables, newOne, head.Count, Math.Round(all.Sum(e => e.LengthM) + (newOne?.StraightM ?? 0)),
            Math.Max(order.Count - 2, 0));
    }

    private async Task<RouteSite?> SiteAsync(UserScope scope, string key, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand($"""
            SELECT s.id, s.code, s.name FROM site s
            WHERE (s.code = $1 OR s.id::text = $1) AND {ScopeSql.Site("s.id", 2)} ORDER BY (s.code = $1) DESC LIMIT 1
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = key.Trim() });
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new RouteSite(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)) : null;
    }
}

/// <summary>Suggests routes over existing cables for a connection between two sites, or a new cable where there is no way (#171).</summary>
public sealed class SuggestRouteEndpoint(RoutePlanner planner) : Endpoint<RouteRequest, RouteSuggestion>
{
    public override void Configure() => Get("/routes/suggest");

    public override async Task HandleAsync(RouteRequest req, CancellationToken ct)
    {
        var result = await planner.SuggestAsync(HttpContext.Scope(), req, ct);
        switch (result.Failure)
        {
            case PlanWriteFailure.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanWriteFailure.None:
                await Send.OkAsync(result.Value!, ct);
                return;
            default:
                AddError(result.Error!);
                await Send.ErrorsAsync(cancellation: ct);
                return;
        }
    }
}

/// <summary>Adds a suggested route to a draft plan: splices through each site in between, and a new cable where needed (#171).</summary>
public sealed class AddRouteEndpoint(RoutePlanner planner) : Endpoint<AddRouteRequest, List<PlanOperationView>>
{
    public override void Configure()
    {
        Post("/plans/{id}/route");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(AddRouteRequest req, CancellationToken ct)
    {
        var result = await planner.AddAsync(User, HttpContext.Scope(), req, ct);
        switch (result.Failure)
        {
            case PlanWriteFailure.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanWriteFailure.Conflict:
                await PlanSql.ConflictAsync(HttpContext, result.Error!, ct);
                return;
            case PlanWriteFailure.None:
                await Send.OkAsync(result.Value!, ct);
                return;
            default:
                AddError(result.Error!);
                await Send.ErrorsAsync(cancellation: ct);
                return;
        }
    }
}

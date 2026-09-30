using Cmdb.Api.Auth;
using System.Diagnostics;
using Cmdb.Api.Features.Objects;
using Cmdb.Graph;
using FastEndpoints;
using FluentValidation;
using Npgsql;

namespace Cmdb.Api.Features.Trace;

public sealed class TraceRequest
{
    /// <summary>Trace in a plan's view (#24) instead of production.</summary>
    [QueryParam]
    public long? Plan { get; set; }

    [QueryParam]
    public long? Terminal { get; set; }

    [QueryParam]
    public long? Service { get; set; }

    [QueryParam]
    public long? Circuit { get; set; }

    /// <summary>Adds the route's geometry for the map (#19): site points and simplified cable lines.</summary>
    [QueryParam]
    public bool Geometry { get; set; }
}

/// <summary>One terminal on a path, named for people: "RAD-000007 BB-6 1 · bh1" or "K-001521 fiber 3 (A)".</summary>
/// <param name="Edge">How the previous hop connects to this one: patch, splice, termination, internal or conductor.</param>
public sealed record TraceHop(long TerminalId, string Kind, string? Edge, string Label, ObjectRef? Equipment, ObjectRef? Cable, ObjectRef? Site, int? Conductor);

/// <param name="Ends">Why each end is where it is: endpoint, branch, loop or limit.</param>
public sealed record TracePath(IReadOnlyList<TraceHop> Hops, int StartIndex, bool Complete, IReadOnlyList<string> Ends);

/// <param name="Depth">0 for the circuits carrying the service; +1 for each layer they ride on.</param>
public sealed record TraceCircuit(ObjectRef Circuit, string Layer, int Depth, long? CarriedCircuitId, IReadOnlyList<TraceHop> Hops);

/// <summary>The route in SWEREF 99 TM, for drawing: site points and cable lines simplified to about 10 m.</summary>
public sealed record TraceRoute(IReadOnlyList<RouteSite> Sites, IReadOnlyList<RouteCable> Cables, double[] Extent);

public sealed record RouteSite(long Id, double X, double Y);

public sealed record RouteCable(long Id, double[][] Coordinates);

/// <param name="Sites">Sites along the physical route in order, for the map.</param>
/// <param name="Cables">Cables along the physical route in order, for the map.</param>
/// <param name="Services">For a terminal: services whose circuits pass it, directly or through circuits above.</param>
/// <param name="Hidden">Circuits and services involved but outside the caller's scope (#22): counted, not shown.</param>
public sealed record TraceResult(
    ObjectRef? Service,
    TracePath? Physical,
    IReadOnlyList<TraceCircuit> Circuits,
    IReadOnlyList<ObjectRef> Sites,
    IReadOnlyList<ObjectRef> Cables,
    IReadOnlyList<ObjectRef> Services,
    double ElapsedMs,
    TraceRoute? Route = null,
    int Hidden = 0);

public sealed class TraceValidator : Validator<TraceRequest>
{
    public TraceValidator() =>
        RuleFor(r => r).Must(r => (r.Terminal.HasValue ? 1 : 0) + (r.Service.HasValue ? 1 : 0) + (r.Circuit.HasValue ? 1 : 0) == 1)
            .WithName("trace").WithMessage("Give exactly one of terminal, service or circuit.");
}

/// <summary>
/// Tracing (#9): the physical route from a terminal through patches, splices and conductors to the equipment at
/// both ends, or a service or circuit down through the layers it rides on. The walk is in memory (ADR-0002); only the
/// names of the few terminals involved come from the database.
/// </summary>
public sealed class TraceEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks, Cmdb.Api.Features.Plans.PlanViews plans) : Endpoint<TraceRequest, TraceResult>
{
    public override void Configure() => Get("/trace");

    public override async Task HandleAsync(TraceRequest req, CancellationToken ct)
    {
        if (holder.Current is not { } graph)
        {
            await Send.ResultAsync(TypedResults.Problem("The network graph is still loading; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable));
            return;
        }
        if (req.Plan is { } planId)
        {
            if (await plans.GetAsync(graph, planId, HttpContext.Scope(), ct) is not { } view)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            graph = view.Graph;
        }
        var mask = await masks.GetAsync(graph, HttpContext.Scope(), ct);
        var result = await RunAsync(graph, mask, db, req.Terminal, req.Service, req.Circuit, ct);
        if (result is not null && req.Geometry)
        {
            result = result with { Route = await RouteAsync(db, result.Sites, result.Cables, HttpContext.Scope(), ct) };
        }
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(result, ct);
    }

    /// <summary>Also used by the MCP tool <c>trace</c>. Null when the start does not exist.</summary>
    /// <remarks>
    /// Access scopes (#22): a start outside the scope does not exist; the physical walk stops at the scope's edge
    /// with a placeholder hop; circuits and services outside it are counted in <see cref="TraceResult.Hidden"/>;
    /// terminals outside it on a visible circuit are placeholders.
    /// </remarks>
    internal static async Task<TraceResult?> RunAsync(Cmdb.Graph.Graph g, GraphMask mask, NpgsqlDataSource db, long? terminal, long? service, long? circuit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        PhysicalPath? physical = null;
        List<CircuitStep> steps = [];
        List<int> services = [];
        var hidden = 0;
        bool Visible(int node) => mask.NodeVisible(g, node);
        if (terminal is { } t)
        {
            if (!g.TryGetNode(t, out var node) || !Visible(node))
            {
                return null;
            }
            physical = GraphTrace.Physical(g, node, Visible);
            foreach (var c in g.CircuitsThrough(node))
            {
                if (mask.CircuitVisible(c))
                {
                    steps.Add(new CircuitStep(c, 0, -1));
                }
                else
                {
                    hidden++;
                }
            }
            var through = GraphTrace.ServicesThrough(g, node);
            services = [.. through.Where(mask.ServiceVisible)];
            hidden += through.Count - services.Count;
        }
        else if (service is { } s)
        {
            if (!g.TryGetService(s, out var index) || !mask.ServiceVisible(index))
            {
                return null;
            }
            steps = GraphTrace.Service(g, index, mask.CircuitVisible);
            hidden = g.CircuitsOf(index).ToArray().Count(c => !mask.CircuitVisible(c));
            services = [index];
        }
        else if (circuit is { } c && g.TryGetCircuit(c, out var index) && mask.CircuitVisible(index))
        {
            steps = GraphTrace.Circuit(g, index, mask.CircuitVisible);
        }
        else
        {
            return null;
        }

        // Every terminal we will show, named in one round trip.
        var terminalIds = new HashSet<long>();
        if (physical is not null)
        {
            foreach (var n in physical.Nodes)
            {
                terminalIds.Add(g.TerminalId(n));
            }
            // The walk only ever includes visible terminals; circuit hops below may not be.
        }
        foreach (var step in steps)
        {
            foreach (var n in g.HopsOf(step.Circuit))
            {
                if (Visible(n))
                {
                    terminalIds.Add(g.TerminalId(n));
                }
            }
        }
        var names = await TraceNames.LoadAsync(db, [.. terminalIds], [.. steps.Select(x => g.CircuitId(x.Circuit))], [.. services.Select(g.ServiceId)], ct);

        TracePath? path = null;
        if (physical is not null)
        {
            // Where the walk met the scope's edge, one neutral placeholder says there is more.
            var hops = physical.Nodes.Select((n, i) => names.Hop(g.TerminalId(n), i == 0 ? null : physical.EdgesBefore[i])).ToList();
            var startIndex = physical.StartIndex;
            if (physical.FirstEnd == TraceEnd.Boundary)
            {
                hops.Insert(0, TraceNames.Placeholder(null));
                startIndex++;
            }
            if (physical.LastEnd == TraceEnd.Boundary)
            {
                hops.Add(TraceNames.Placeholder(null));
            }
            path = new TracePath(hops, startIndex, physical.Complete, [End(physical.FirstEnd), End(physical.LastEnd)]);
        }

        var circuits = steps.Select(step =>
        {
            var hops = g.HopsOf(step.Circuit).ToArray();
            return new TraceCircuit(
                names.Circuit(g.CircuitId(step.Circuit)),
                Layer(g.LayerOf(step.Circuit)),
                step.Depth,
                step.Parent < 0 ? null : g.CircuitId(step.Parent),
                [.. hops.Select((n, i) =>
                {
                    var edge = i == 0 ? null : EdgeBetween(g, hops[i - 1], n);
                    return Visible(n) ? names.Hop(g.TerminalId(n), edge) : TraceNames.Placeholder(edge);
                })]);
        }).ToList();

        // The route on the map: the physical path, or the physical circuits in the order they were reached.
        var route = path?.Hops ?? [.. circuits.Where(c => c.Layer == "physical").SelectMany(c => c.Hops)];
        var sites = Distinct(route.Select(h => h.Site));
        var cables = Distinct(route.Select(h => h.Cable));

        return new TraceResult(
            service is not null && services.Count > 0 ? names.Service(g.ServiceId(services[0])) : null,
            path,
            circuits,
            sites,
            cables,
            terminal is not null ? [.. services.Select(i => names.Service(g.ServiceId(i)))] : [],
            Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            Hidden: hidden);
    }

    /// <summary>How two consecutive hops connect, when they are adjacent in the graph (physical circuits).</summary>
    private static EdgeKind? EdgeBetween(Cmdb.Graph.Graph g, int from, int to)
    {
        var neighbours = g.Neighbours(from);
        for (var i = 0; i < neighbours.Length; i++)
        {
            if (neighbours[i] == to)
            {
                return g.NeighbourKinds(from)[i];
            }
        }
        return null;
    }

    private static async Task<TraceRoute> RouteAsync(NpgsqlDataSource db, IReadOnlyList<ObjectRef> sites, IReadOnlyList<ObjectRef> cables, UserScope scope, CancellationToken ct)
    {
        if (scope.HidesCoordinates)
        {
            return new TraceRoute([], [], []);
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new("SELECT id, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site WHERE id = ANY($1)")
                {
                    Parameters = { new() { Value = sites.Select(s => s.Id).ToArray() } },
                },
                new($"""
                    SELECT c.id, ARRAY(SELECT ARRAY[round(ST_X(p.geom)), round(ST_Y(p.geom))]
                                       FROM ST_DumpPoints(ST_Simplify({ScopeSql.CableGeometry("c", 2, scope)}, 10)) p ORDER BY p.path)
                    FROM cable c WHERE c.id = ANY($1)
                    """)
                {
                    Parameters = { new() { Value = cables.Select(c => c.Id).ToArray() }, scope.Parameter() },
                },
            },
        };
        var points = new Dictionary<long, RouteSite>();
        var lines = new Dictionary<long, RouteCable>();
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                points[reader.GetInt64(0)] = new RouteSite(reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2));
            }
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // A cable cut away entirely at a clipping scope's edge has no points.
                if (reader.GetValue(1) is not double[,] coordinates || coordinates.Length == 0)
                {
                    continue;
                }
                lines[reader.GetInt64(0)] = new RouteCable(
                    reader.GetInt64(0),
                    [.. Enumerable.Range(0, coordinates.GetLength(0)).Select(i => new[] { coordinates[i, 0], coordinates[i, 1] })]);
            }
        }
        // In route order, like the lists they come from.
        var routeSites = sites.Where(s => points.ContainsKey(s.Id)).Select(s => points[s.Id]).ToList();
        var routeCables = cables.Where(c => lines.ContainsKey(c.Id)).Select(c => lines[c.Id]).ToList();
        var xs = routeSites.Select(s => s.X).Concat(routeCables.SelectMany(c => c.Coordinates.Select(p => p[0]))).ToList();
        var ys = routeSites.Select(s => s.Y).Concat(routeCables.SelectMany(c => c.Coordinates.Select(p => p[1]))).ToList();
        double[] extent = xs.Count == 0 ? [] : [xs.Min(), ys.Min(), xs.Max(), ys.Max()];
        return new TraceRoute(routeSites, routeCables, extent);
    }

    private static List<ObjectRef> Distinct(IEnumerable<ObjectRef?> refs)
    {
        var seen = new HashSet<long>();
        var list = new List<ObjectRef>();
        foreach (var r in refs)
        {
            if (r is not null && seen.Add(r.Id))
            {
                list.Add(r);
            }
        }
        return list;
    }

    internal static string End(TraceEnd end) => end switch
    {
        TraceEnd.Endpoint => "endpoint",
        TraceEnd.Branch => "branch",
        TraceEnd.Loop => "loop",
        TraceEnd.Boundary => "boundary",
        _ => "limit",
    };

    internal static string Layer(CircuitLayer layer) => layer switch
    {
        CircuitLayer.Physical => "physical",
        CircuitLayer.Transmission => "transmission",
        _ => "logical",
    };

    internal static string Edge(EdgeKind kind) => kind switch
    {
        EdgeKind.Patch => "patch",
        EdgeKind.Splice => "splice",
        EdgeKind.Termination => "termination",
        EdgeKind.Internal => "internal",
        _ => "conductor",
    };
}

/// <summary>Names for the terminals, circuits and services in a trace, fetched in one batch.</summary>
internal sealed class TraceNames
{
    private readonly Dictionary<long, (string Kind, string Label, ObjectRef? Equipment, ObjectRef? Cable, ObjectRef? Site, int? Conductor)> _terminals = [];
    private readonly Dictionary<long, ObjectRef> _circuits = [];
    private readonly Dictionary<long, ObjectRef> _services = [];

    public static async Task<TraceNames> LoadAsync(NpgsqlDataSource db, long[] terminals, long[] circuits, long[] services, CancellationToken ct)
    {
        var names = new TraceNames();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new("""
                    SELECT p.terminal_id, p.name, e.id, e.name, e.lifecycle::text, s.id, s.code, s.name, s.lifecycle::text
                    FROM port p JOIN equipment e ON e.id = p.equipment_id JOIN site s ON s.id = e.site_id
                    WHERE p.terminal_id = ANY($1)
                    """) { Parameters = { new() { Value = terminals } } },
                new("""
                    SELECT ce.terminal_id, co.number, ce.side, c.id, c.code, c.lifecycle::text, s.id, s.code, s.name, s.lifecycle::text
                    FROM conductor_end ce JOIN conductor co ON co.id = ce.conductor_id JOIN cable c ON c.id = co.cable_id
                    JOIN site s ON s.id = CASE ce.side WHEN 'A' THEN c.a_site_id ELSE c.b_site_id END
                    WHERE ce.terminal_id = ANY($1)
                    """) { Parameters = { new() { Value = terminals } } },
                new("SELECT id, code, lifecycle::text FROM circuit WHERE id = ANY($1)") { Parameters = { new() { Value = circuits } } },
                new("SELECT id, code, name, lifecycle::text FROM service WHERE id = ANY($1)") { Parameters = { new() { Value = services } } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var equipment = new ObjectRef("equipment", reader.GetInt64(2), reader.GetString(3), null, reader.GetString(4));
            var site = new ObjectRef("site", reader.GetInt64(5), reader.GetString(6), reader.GetString(7), reader.GetString(8));
            names._terminals[reader.GetInt64(0)] = ("port", $"{equipment.Code} · {reader.GetString(1)}", equipment, null, site, null);
        }
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var number = reader.GetInt32(1);
            var cable = new ObjectRef("cable", reader.GetInt64(3), reader.GetString(4), null, reader.GetString(5));
            var site = new ObjectRef("site", reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetString(9));
            names._terminals[reader.GetInt64(0)] = ("conductor_end", $"{cable.Code} ledare {number} ({reader.GetString(2).Trim()})", null, cable, site, number);
        }
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names._circuits[reader.GetInt64(0)] = new ObjectRef("circuit", reader.GetInt64(0), reader.GetString(1), null, reader.GetString(2));
        }
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names._services[reader.GetInt64(0)] = new ObjectRef("service", reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }
        return names;
    }

    /// <summary>A terminal outside the caller's scope (#22): no id, no names, only that the path goes on.</summary>
    public static TraceHop Placeholder(EdgeKind? edge) =>
        new(0, "hidden", edge is { } e ? TraceEndpoint.Edge(e) : null, "Utanför ditt omfång", null, null, null, null);

    public TraceHop Hop(long terminal, EdgeKind? edge)
    {
        var edgeName = edge is { } e ? TraceEndpoint.Edge(e) : null;
        return _terminals.TryGetValue(terminal, out var t)
            ? new TraceHop(terminal, t.Kind, edgeName, t.Label, t.Equipment, t.Cable, t.Site, t.Conductor)
            : new TraceHop(terminal, "unknown", edgeName, $"terminal {terminal}", null, null, null, null);
    }

    public ObjectRef Circuit(long id) => _circuits.GetValueOrDefault(id) ?? new ObjectRef("circuit", id, $"#{id}");

    public ObjectRef Service(long id) => _services.GetValueOrDefault(id) ?? new ObjectRef("service", id, $"#{id}");
}

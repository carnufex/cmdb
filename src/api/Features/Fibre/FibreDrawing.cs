using System.Diagnostics;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Conduit;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Plans;
using Cmdb.Api.Features.Trace;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Fibre;

/// <summary>A fibre drawing (#254) of a cable, a route segment, a site or a selection, for a technician to take out.</summary>
public sealed class FibreRequest
{
    /// <summary>cables, route-segments or sites.</summary>
    public string Type { get; set; } = "";
    public long Id { get; set; }

    /// <summary>The drawing in a plan's view (#24): an intervention drawn before and after.</summary>
    [QueryParam]
    public long? Plan { get; set; }

    /// <summary>A break point in SWEREF 99 TM (#254): the distances along the cable to its nearest splice or ODF.</summary>
    [QueryParam]
    public double? X { get; set; }

    [QueryParam]
    public double? Y { get; set; }
}

public sealed class FibreSelectionRequest
{
    public long[] SiteIds { get; set; } = [];
    public long[] CableIds { get; set; } = [];

    [QueryParam]
    public long? Plan { get; set; }
}

/// <param name="A">What the fibre is connected to at the A end: spliced to another cable's fibre, or a port (an ODF).</param>
/// <param name="Usage">lit (a circuit runs on it, in the plan's view too), or what is stated: dark, dark_fibre (leased) or spare.</param>
/// <param name="Services">How many services the caller may see depend on the fibre; they are listed in <see cref="FibreDrawing.Services"/>.</param>
public sealed record FibreConductor(int Number, string? Color, string Usage, bool Reserved, IReadOnlyList<FibreEnd> A, IReadOnlyList<FibreEnd> B,
    IReadOnlyList<ObjectRef> Circuits, int Services);

/// <summary>What a fibre end is connected to: a port or another cable's fibre end, by name; hidden outside the caller's scope.</summary>
/// <param name="Kind">port, conductor_end or hidden.</param>
/// <param name="Edge">The connection: splice, termination or patch.</param>
public sealed record FibreEnd(long TerminalId, string Kind, string? Edge, string Label);

public sealed record FibreCable(ObjectRef Cable, string TypeName, string? ColorCode, int Conductors, double LengthM, ObjectRef A, ObjectRef B,
    IReadOnlyList<FibreConductor> Fibres);

/// <param name="Criticality">The service's criticality level (#176), when it has one.</param>
public sealed record FibreService(ObjectRef Service, int? Criticality, bool Critical);

/// <summary>Where a break point lies along a cable: the distances along its route to each end, where it is spliced or terminated.</summary>
/// <param name="Off">How far the chosen point lies from the cable; a point far off is likely a misclick.</param>
public sealed record FibreBreak(ObjectRef Cable, double ToA, double ToB, ObjectRef A, ObjectRef B, double Off);

/// <param name="Cables">The cables in the drawing, ordered by code.</param>
/// <param name="HiddenCables">Cables in the area outside the caller's scope (#22): counted, not drawn.</param>
/// <param name="Center">For a single cable, the middle of its route, for the map to show while a break point is chosen.</param>
public sealed record FibreDrawing(string Title, IReadOnlyList<FibreCable> Cables, IReadOnlyList<FibreService> Services, int HiddenServices,
    int HiddenCables, FibreBreak? Break, double ElapsedMs, double[]? Center = null);

/// <summary>
/// The fibre drawing (#254): the cables of a cable, a route segment, a site or a selection, fibre by fibre, with what each
/// fibre is spliced or terminated to at both ends, what it carries, and the services that depend on it with their
/// criticality. Connections, circuits and services come from the graph, so a plan's view draws the intervention before and
/// after. What the caller cannot see is hidden (#22). Budget: 200 ms for a route segment (docs/plan.md).
/// </summary>
public sealed class FibreDrawingEndpoint(FibreDrawings drawings) : Endpoint<FibreRequest, FibreDrawing>
{
    public override void Configure() => Get("/fibre/{type}/{id}");

    public override async Task HandleAsync(FibreRequest req, CancellationToken ct)
    {
        var result = await drawings.DrawAsync(HttpContext.Scope(), req.Type, req.Id, [], req.Plan, req.X, req.Y, ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(result, ct);
    }
}

/// <summary>The fibre drawing of a selection (#253, #254): its cables, and the cables between its sites.</summary>
public sealed class FibreSelectionEndpoint(FibreDrawings drawings) : Endpoint<FibreSelectionRequest, FibreDrawing>
{
    public override void Configure() => Post("/fibre/selection");

    public override async Task HandleAsync(FibreSelectionRequest req, CancellationToken ct)
    {
        var result = await drawings.DrawAsync(HttpContext.Scope(), "selection", 0, req.SiteIds, req.Plan, null, null, ct, req.CableIds);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(result, ct);
    }
}

public sealed class FibreDrawings(RequestDb db, GraphHolder holder, ScopeMasks masks, PlanViews plans, ConduitIndex conduit)
{
    /// <summary>The most cables a drawing takes; a site or selection with more is cut at this, by code.</summary>
    public const int MaxCables = 60;

    internal async Task<FibreDrawing?> DrawAsync(UserScope scope, string type, long id, long[] siteIds, long? planId, double? x, double? y,
        CancellationToken ct, long[]? cableIds = null)
    {
        var sw = Stopwatch.StartNew();
        if (holder.Current is not { } production)
        {
            return null;
        }
        var graph = production;
        PlanView? view = null;
        if (planId is { } p)
        {
            if ((view = await plans.GetAsync(production, p, scope, ct)) is null)
            {
                return null;
            }
            graph = view.Graph;
        }
        var mask = await masks.GetAsync(graph, scope, ct);

        // The cables in the drawing, and the title.
        long[] candidates;
        string title;
        switch (type)
        {
            case "cables":
                candidates = [id];
                title = await CodeAsync("cable", id, scope, ct) is { } cable ? $"Kabel {cable}" : "";
                break;
            case "route-segments":
                if (await CodeAsync("route_segment", id, scope, ct) is not { } segment)
                {
                    return null;
                }
                candidates = [.. (await conduit.GetAsync(production, ct)).GetValueOrDefault(id, []).Select(production.CableId)];
                title = $"Trasésträcka {segment}";
                break;
            case "sites":
                if (await CodeAsync("site", id, scope, ct) is not { } site)
                {
                    return null;
                }
                candidates = await CableIdsAsync("SELECT id FROM cable WHERE (a_site_id = $1 OR b_site_id = $1) AND lifecycle <> 'removed'", id, ct);
                title = $"Site {site}";
                break;
            case "selection":
                candidates = [.. (cableIds ?? []).Concat(await CablesBetweenAsync(siteIds, ct)).Distinct()];
                title = "Urval";
                break;
            default:
                return null;
        }
        if (type == "cables" && title == "")
        {
            return null;
        }

        var visible = new List<long>();
        var hidden = 0;
        foreach (var cableId in candidates.Distinct())
        {
            if (graph.TryGetCable(cableId, out var c) && mask.CableVisible(c))
            {
                visible.Add(cableId);
            }
            else if (graph.TryGetCable(cableId, out _))
            {
                hidden++;
            }
        }

        var cables = await LoadAsync(visible, ct);
        cables = [.. cables.OrderBy(c => c.Code, StringComparer.Ordinal).Take(MaxCables)];

        // What each fibre end is connected to, its circuits and its services, from the graph.
        var endNodes = new Dictionary<long, int>();
        var terminals = new HashSet<long>();
        foreach (var k in cables.SelectMany(c => c.Fibres))
        {
            foreach (var t in new[] { k.ATerminal, k.BTerminal })
            {
                if (graph.TryGetNode(t, out var node))
                {
                    endNodes[t] = node;
                    // What the end connects to; the fibre's own other end is the cable, and needs no name.
                    var targets = graph.Neighbours(node);
                    var kinds = graph.NeighbourKinds(node);
                    for (var i = 0; i < targets.Length; i++)
                    {
                        if (kinds[i] != EdgeKind.Conductor)
                        {
                            terminals.Add(graph.TerminalId(targets[i]));
                        }
                    }
                }
            }
        }
        // Each fibre's circuits, and the services above them: worked out once per circuit and shared, since thousands of
        // fibres ride on the same transmission and logical circuits (one impact walk per fibre took most of the time).
        var circuitsPerFibre = new Dictionary<(long, int), int[]>();
        var servicesPerFibre = new Dictionary<(long, int), int>();
        var circuitIds = new HashSet<long>();
        var serviceIndexes = new HashSet<int>();
        var above = new Dictionary<int, int[]>();
        foreach (var cable in cables)
        {
            foreach (var k in cable.Fibres)
            {
                var direct = new HashSet<int>();
                foreach (var t in new[] { k.ATerminal, k.BTerminal })
                {
                    if (endNodes.TryGetValue(t, out var node))
                    {
                        foreach (var c in graph.CircuitsThrough(node))
                        {
                            direct.Add(c);
                        }
                    }
                }
                var directArray = direct.Order().ToArray();
                circuitsPerFibre[(cable.Id, k.Number)] = directArray;
                var reached = directArray.Length == 1 ? ServicesAbove(graph, directArray[0], above)
                    : [.. directArray.SelectMany(c => ServicesAbove(graph, c, above)).Distinct()];
                servicesPerFibre[(cable.Id, k.Number)] = reached.Count(mask.ServiceVisible);
                foreach (var c in directArray.Where(mask.CircuitVisible))
                {
                    circuitIds.Add(graph.CircuitId(c));
                }
                foreach (var sv in reached)
                {
                    serviceIndexes.Add(sv);
                }
            }
        }
        var shownServices = serviceIndexes.Where(mask.ServiceVisible).ToArray();
        var names = await NamesAsync([.. terminals], [.. circuitIds], [.. shownServices.Select(graph.ServiceId)], ct);
        if (view is not null)
        {
            foreach (var (terminal, hop) in (await PlannedNames.BuildAsync(db, view.Chain.Operations, ct)).Terminals)
            {
                names.Terminals[terminal] = (hop.Kind, hop.Label);
            }
        }
        var criticality = await CriticalityAsync([.. shownServices.Select(graph.ServiceId)], ct);
        var schema = ClassificationCatalog.Current.Find("criticality");

        List<FibreEnd> Connected(long terminal)
        {
            if (!endNodes.TryGetValue(terminal, out var node))
            {
                return [];
            }
            var hops = new List<FibreEnd>();
            var targets = graph.Neighbours(node);
            var kinds = graph.NeighbourKinds(node);
            for (var i = 0; i < targets.Length; i++)
            {
                // The fibre's own other end is the cable, not a connection.
                if (kinds[i] == EdgeKind.Conductor)
                {
                    continue;
                }
                var edge = TraceEndpoint.Edge(kinds[i]);
                if (!VisibleNode(graph, mask, targets[i]))
                {
                    hops.Add(new FibreEnd(0, "hidden", edge, "Utanför ditt omfång"));
                    continue;
                }
                var id = graph.TerminalId(targets[i]);
                var (kind, label) = names.Terminals.GetValueOrDefault(id, ("unknown", $"terminal {id}"));
                hops.Add(new FibreEnd(id, kind, edge, label));
            }
            return hops;
        }

        var drawn = cables.Select(c => new FibreCable(
            new ObjectRef("cable", c.Id, c.Code, null, c.Lifecycle), c.TypeName, c.ColorCode, c.Fibres.Count, c.LengthM, c.A, c.B,
            [.. c.Fibres.Select(k =>
            {
                var direct = circuitsPerFibre[(c.Id, k.Number)];
                return new FibreConductor(k.Number, k.Color,
                    direct.Length > 0 ? "lit" : k.Usage ?? "dark",
                    k.Reserved, Connected(k.ATerminal), Connected(k.BTerminal),
                    [.. direct.Select(d => mask.CircuitVisible(d)
                        ? names.Circuits.GetValueOrDefault(graph.CircuitId(d)) ?? new ObjectRef("circuit", graph.CircuitId(d), $"#{graph.CircuitId(d)}")
                        : ObjectRef.Hidden("circuit"))],
                    servicesPerFibre[(c.Id, k.Number)]);
            })])).ToList();
        var services = shownServices
            .Select(s => graph.ServiceId(s))
            .Select(sid => new FibreService(names.Services.GetValueOrDefault(sid) ?? new ObjectRef("service", sid, $"#{sid}"), criticality.GetValueOrDefault(sid),
                schema is not null && criticality.TryGetValue(sid, out var level) && level >= schema.CriticalFrom))
            .OrderByDescending(s => s.Criticality ?? 0).ThenBy(s => s.Service.Code, StringComparer.Ordinal)
            .ToList();

        FibreBreak? brk = null;
        double[]? center = null;
        if (type == "cables" && drawn.Count == 1)
        {
            (brk, center) = await BreakAsync(drawn[0], x, y, ct);
        }
        return new FibreDrawing(title, drawn, services, serviceIndexes.Count - shownServices.Length, hidden, brk,
            Math.Round(sw.Elapsed.TotalMilliseconds, 1), center);
    }

    private static bool VisibleNode(Cmdb.Graph.Graph g, GraphMask mask, int node) => mask.NodeVisible(g, node);

    /// <summary>
    /// The services that depend on a circuit: its own and those of every circuit riding on it, upwards, as impact
    /// analysis finds them (#10). Memoized per circuit for the drawing; the layers form a DAG, so each is worked out once.
    /// </summary>
    private static int[] ServicesAbove(Cmdb.Graph.Graph g, int circuit, Dictionary<int, int[]> memo)
    {
        if (memo.TryGetValue(circuit, out var known))
        {
            return known;
        }
        memo[circuit] = [];
        var services = new HashSet<int>(g.ServicesOf(circuit).ToArray());
        foreach (var dependent in g.DependentsOf(circuit))
        {
            services.UnionWith(ServicesAbove(g, dependent, memo));
        }
        return memo[circuit] = [.. services];
    }

    private sealed record Names(Dictionary<long, (string Kind, string Label)> Terminals, Dictionary<long, ObjectRef> Circuits,
        Dictionary<long, ObjectRef> Services);

    /// <summary>
    /// Names for what the fibre ends connect to, their circuits and the services, in one round trip. Leaner than the trace's
    /// names: only a label per terminal, and the arrays joined with unnest, which stays fast for the thousands of terminals
    /// a busy route segment has.
    /// </summary>
    private async Task<Names> NamesAsync(long[] terminals, long[] circuits, long[] services, CancellationToken ct)
    {
        var names = new Names([], [], []);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new("""
                    SELECT p.terminal_id, 'port', e.name || ' · ' || p.name
                    FROM unnest($1::bigint[]) u(t) JOIN port p ON p.terminal_id = u.t JOIN equipment e ON e.id = p.equipment_id
                    UNION ALL
                    SELECT ce.terminal_id, 'conductor_end', c.code || ' ledare ' || co.number || ' (' || trim(ce.side) || ')'
                    FROM unnest($1::bigint[]) u(t) JOIN conductor_end ce ON ce.terminal_id = u.t
                    JOIN conductor co ON co.id = ce.conductor_id JOIN cable c ON c.id = co.cable_id
                    """) { Parameters = { new() { Value = terminals } } },
                new("SELECT r.id, r.code, r.lifecycle::text FROM unnest($1::bigint[]) u(id) JOIN circuit r ON r.id = u.id")
                    { Parameters = { new() { Value = circuits } } },
                new("SELECT v.id, v.code, v.name, v.lifecycle::text FROM unnest($1::bigint[]) u(id) JOIN service v ON v.id = u.id")
                    { Parameters = { new() { Value = services } } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Terminals[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2));
        }
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Circuits[reader.GetInt64(0)] = new ObjectRef("circuit", reader.GetInt64(0), reader.GetString(1), null, reader.GetString(2));
        }
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Services[reader.GetInt64(0)] = new ObjectRef("service", reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }
        return names;
    }

    private sealed record LoadedFibre(int Number, string? Color, string? Usage, bool Reserved, long ATerminal, long BTerminal);

    private sealed record LoadedCable(long Id, string Code, string Lifecycle, string TypeName, string? ColorCode, double LengthM, ObjectRef A,
        ObjectRef B, List<LoadedFibre> Fibres);

    private async Task<List<LoadedCable>> LoadAsync(List<long> ids, CancellationToken ct)
    {
        var cables = new Dictionary<long, LoadedCable>();
        if (ids.Count == 0)
        {
            return [];
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new("""
                    SELECT c.id, c.code, c.lifecycle::text, ct.name, ct.color_code, c.length_m,
                           a.id, a.code, a.name, a.lifecycle::text, b.id, b.code, b.name, b.lifecycle::text
                    FROM cable c JOIN cable_type ct ON ct.id = c.cable_type_id JOIN site a ON a.id = c.a_site_id JOIN site b ON b.id = c.b_site_id
                    WHERE c.id = ANY($1)
                    """) { Parameters = { new() { Value = ids.ToArray() } } },
                new("""
                    SELECT k.cable_id, k.number, k.color, k.usage,
                           EXISTS (SELECT 1 FROM reservation v WHERE v.resource_kind = 'conductor' AND v.resource_id = k.id AND v.released_at IS NULL),
                           (SELECT e.terminal_id FROM conductor_end e WHERE e.conductor_id = k.id AND e.side = 'A'),
                           (SELECT e.terminal_id FROM conductor_end e WHERE e.conductor_id = k.id AND e.side = 'B')
                    FROM conductor k WHERE k.cable_id = ANY($1) ORDER BY k.cable_id, k.number
                    """) { Parameters = { new() { Value = ids.ToArray() } } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            cables[id] = new LoadedCable(id, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDouble(5),
                new ObjectRef("site", reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetString(9)),
                new ObjectRef("site", reader.GetInt64(10), reader.GetString(11), reader.GetString(12), reader.GetString(13)), []);
        }
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (cables.TryGetValue(reader.GetInt64(0), out var cable))
            {
                cable.Fibres.Add(new LoadedFibre(reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5), reader.GetInt64(6)));
            }
        }
        return [.. cables.Values];
    }

    private async Task<string?> CodeAsync(string table, long id, UserScope scope, CancellationToken ct)
    {
        var visible = table switch
        {
            "cable" => ScopeSql.Cable("t.id", 2),
            "site" => ScopeSql.Site("t.id", 2),
            _ => ScopeSql.RouteSegment("t.id", 2),
        };
        await using var cmd = db.CreateCommand($"SELECT t.code FROM {table} t WHERE t.id = $1 AND {visible}");
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(scope.Parameter());
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private async Task<long[]> CableIdsAsync(string sql, object value, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = value });
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return [.. ids];
    }

    /// <summary>The cables with both ends among the selected sites.</summary>
    private Task<long[]> CablesBetweenAsync(long[] sites, CancellationToken ct) => sites.Length < 2
        ? Task.FromResult<long[]>([])
        : CableIdsAsync("SELECT id FROM cable WHERE a_site_id = ANY($1) AND b_site_id = ANY($1) AND lifecycle <> 'removed'", sites, ct);

    private async Task<Dictionary<long, int>> CriticalityAsync(long[] services, CancellationToken ct)
    {
        var levels = new Dictionary<long, int>();
        await using var cmd = db.CreateCommand("""
            SELECT object_id, level FROM classification WHERE object_type = 'service' AND schema_key = 'criticality' AND object_id = ANY($1)
            """);
        cmd.Parameters.Add(new() { Value = services });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            levels[reader.GetInt64(0)] = reader.GetInt32(1);
        }
        return levels;
    }

    /// <summary>The middle of the cable's route and, with a point, the distances along the route from it to each end.</summary>
    private async Task<(FibreBreak?, double[]?)> BreakAsync(FibreCable cable, double? x, double? y, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH c AS (SELECT geom, ST_SetSRID(ST_MakePoint(coalesce($2, 0), coalesce($3, 0)), 3006) AS p FROM cable WHERE id = $1 AND GeometryType(geom) = 'LINESTRING')
            SELECT ST_X(ST_LineInterpolatePoint(geom, 0.5)), ST_Y(ST_LineInterpolatePoint(geom, 0.5)),
                   ST_LineLocatePoint(geom, p), ST_Length(geom), ST_Distance(geom, p)
            FROM c
            """);
        cmd.Parameters.Add(new() { Value = cable.Cable.Id });
        cmd.Parameters.Add(new() { Value = (object?)x ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double });
        cmd.Parameters.Add(new() { Value = (object?)y ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (null, null);
        }
        double[] center = [Math.Round(reader.GetDouble(0)), Math.Round(reader.GetDouble(1))];
        if (x is null || y is null)
        {
            return (null, center);
        }
        var (fraction, length) = (reader.GetDouble(2), reader.GetDouble(3));
        return (new FibreBreak(cable.Cable, Math.Round(fraction * length), Math.Round((1 - fraction) * length), cable.A, cable.B,
            Math.Round(reader.GetDouble(4))), center);
    }
}

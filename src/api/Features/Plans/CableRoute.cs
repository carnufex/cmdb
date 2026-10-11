using System.Diagnostics;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>
/// One step when drawing a cable (#259): a route segment to follow, a site to go to, or a free break point. A new stretch
/// to a site or a point is dug (trench) or hung (aerial).
/// </summary>
public sealed class CableRouteStep
{
    /// <summary>segment, site or point.</summary>
    public string Kind { get; set; } = "";

    /// <summary>segment: the route segment; site: the site, existing or planned.</summary>
    public long? Id { get; set; }

    /// <summary>point: where the break point is, in EPSG:3006.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }

    /// <summary>A new stretch to this step: trench or aerial (default trench).</summary>
    public string? Construction { get; set; }

    /// <summary>segment: dig a new trench beside it instead of using its ducts, for a segment whose tubes are full.</summary>
    public bool Beside { get; set; }
}

/// <summary>A part of the drawn cable: along a route segment in one of its tubes, or a new stretch.</summary>
/// <param name="Kind">segment (existing conduit) or new (a new route segment in the plan).</param>
/// <param name="Subduct">The empty tube the cable goes in; null when the segment's tubes are full (see warnings).</param>
/// <param name="ToSite">The site the leg ends at; null at a free break point.</param>
public sealed record CableRouteLeg(string Kind, ObjectRef? Segment, string Construction, double LengthM, double[][] Coordinates,
    long? ToSite, int? FreeTubes = null, int? Tubes = null, long? Subduct = null);

/// <summary>A drawn cable worked out (#259): its line, its length in existing conduit and new, and where it ends.</summary>
/// <param name="Complete">The cable ends at another site than it starts at, so it can be added.</param>
public sealed record CableRoute(double[][] Coordinates, double LengthM, double InConduitM, double NewM, IReadOnlyList<CableRouteLeg> Legs,
    ObjectRef? End, bool Complete, IReadOnlyList<string> Warnings, double ElapsedMs);

public sealed class CableRouteRequest
{
    public long Id { get; set; }
    public long ASiteId { get; set; }
    public CableRouteStep[] Steps { get; set; } = [];
}

/// <summary>
/// Works out a drawn cable (#259): a followed segment takes the segment's geometry from the end the cable is at to the
/// other, a site or point not reached by a segment is a new stretch, and each followed segment gets an empty tube that
/// nothing earlier in the cable, the plan or the plans it builds on has taken. Used for the drawing's live preview and to
/// check a cable added with a route.
/// </summary>
public sealed class CableRoutes(RequestDb db)
{
    public const int MaxSteps = 200;

    private sealed record Place(long? Site, string? Code, double X, double Y);

    /// <param name="except">A planned cable being drawn again: its own tubes are free to it.</param>
    public async Task<(CableRoute? Route, string? Error)> ResolveAsync(UserScope scope, PlanView view, long aSiteId, IReadOnlyList<CableRouteStep> steps,
        CancellationToken ct, long? except = null)
    {
        var sw = Stopwatch.StartNew();
        if (steps.Count > MaxSteps)
        {
            return (null, $"Högst {MaxSteps} steg.");
        }
        var start = await SiteAsync(scope, view, aSiteId, ct);
        if (start is null)
        {
            return (null, $"Site {aSiteId} finns inte.");
        }
        var taken = await TakenAsync(view, except, ct);
        var mine = new HashSet<long>();
        var legs = new List<CableRouteLeg>();
        var warnings = new List<string>();
        var at = start;
        foreach (var step in steps)
        {
            var construction = step.Construction is "aerial" ? "aerial" : "trench";
            switch (step.Kind)
            {
                case "point" when step.X is { } x && step.Y is { } y:
                    var point = new Place(null, null, x, y);
                    legs.Add(New(at, point, construction));
                    at = point;
                    break;
                case "site" when step.Id is { } siteId && siteId == at.Site:
                    // A click on the site the cable has just reached: nothing more to draw.
                    break;
                case "site" when step.Id is { } siteId:
                    {
                        var site = await SiteAsync(scope, view, siteId, ct);
                        if (site is null)
                        {
                            return (null, $"Site {siteId} finns inte.");
                        }
                        // A segment straight between the two sites is followed; otherwise a new stretch.
                        var direct = at.Site is { } from ? await DirectSegmentAsync(scope, from, siteId, ct) : null;
                        if (direct is not null)
                        {
                            legs.Add(await FollowAsync(direct.Value, at.Site!.Value, taken, mine, warnings, ct));
                        }
                        else
                        {
                            legs.Add(New(at, site, construction));
                        }
                        at = site;
                        break;
                    }
                case "segment" when step.Id is { } segmentId:
                    {
                        var segment = await SegmentAsync(scope, segmentId, ct);
                        if (segment is null)
                        {
                            return (null, $"Trasésträckan {segmentId} finns inte.");
                        }
                        var (a, b, coords) = segment.Value;
                        // From the end the cable is at; from anywhere else, a new stretch to the nearer end first.
                        Place near;
                        if (at.Site == a.Site || at.Site == b.Site)
                        {
                            near = at.Site == a.Site ? a : b;
                        }
                        else
                        {
                            near = Distance(at, a) <= Distance(at, b) ? a : b;
                            legs.Add(New(at, near, construction));
                        }
                        var forward = near.Site == a.Site;
                        if (step.Beside)
                        {
                            var line = forward ? coords : [.. coords.Reverse()];
                            legs.Add(new CableRouteLeg("new", null, "trench", Length(line), line, (forward ? b : a).Site));
                        }
                        else
                        {
                            legs.Add(await FollowAsync((segmentId, coords, a, b), near.Site!.Value, taken, mine, warnings, ct));
                        }
                        at = forward ? b : a;
                        break;
                    }
                default:
                    return (null, $"Okänt steg: {step.Kind}.");
            }
        }

        var coordinates = Join(start, legs);
        var inConduit = legs.Where(l => l.Kind == "segment").Sum(l => l.LengthM);
        var fresh = legs.Where(l => l.Kind == "new").Sum(l => l.LengthM);
        var end = at.Site is { } endSite ? new ObjectRef("site", endSite, at.Code ?? "") : null;
        var complete = end is not null && end.Id != aSiteId && legs.Count > 0;
        return (new CableRoute(coordinates, Math.Round(inConduit + fresh), Math.Round(inConduit), Math.Round(fresh), legs, end, complete, warnings,
            Math.Round(sw.Elapsed.TotalMilliseconds, 1)), null);
    }

    /// <summary>
    /// A drawn cable's fields in its operation (#259): the line between its ends, its length, the tubes per segment, the new
    /// segments, and the steps as drawn so it can be drawn again.
    /// </summary>
    public static void Write(System.Text.Json.Nodes.JsonObject payload, long aSiteId, CableRoute route, IReadOnlyList<CableRouteStep> steps)
    {
        payload["line"] = JsonSerializer.SerializeToNode(route.Coordinates[1..^1]);
        payload["lengthM"] = route.LengthM;
        payload["route"] = JsonSerializer.SerializeToNode(route.Legs.Select(l => new { kind = l.Kind, segment = l.Segment?.Id, subduct = l.Subduct, construction = l.Construction }));
        payload["newSegments"] = JsonSerializer.SerializeToNode(NewSegments(aSiteId, route).Select(n => new { a = n.A, b = n.B, construction = n.Construction, line = n.Line }));
        payload["steps"] = JsonSerializer.SerializeToNode(steps.Select(s => new { kind = s.Kind, id = s.Id, x = s.X, y = s.Y, construction = s.Construction, beside = s.Beside }));
    }

    /// <summary>
    /// The new route segments a route needs (#259): consecutive new stretches from one site to the next become one segment,
    /// hung (aerial) when any part of it is, dug otherwise.
    /// </summary>
    public static List<(long A, long B, string Construction, double[][] Line)> NewSegments(long aSiteId, CableRoute route)
    {
        var result = new List<(long, long, string, double[][])>();
        var from = aSiteId;
        var run = new List<CableRouteLeg>();
        foreach (var leg in route.Legs)
        {
            if (leg.Kind == "segment")
            {
                run.Clear();
                from = leg.ToSite ?? from;
                continue;
            }
            run.Add(leg);
            if (leg.ToSite is { } reached && reached != from)
            {
                result.Add((from, reached, run.Any(l => l.Construction == "aerial") ? "aerial" : "trench", Join(run)));
                run.Clear();
                from = reached;
            }
        }
        return result;
    }

    private static CableRouteLeg New(Place from, Place to, string construction)
    {
        double[][] line = [[from.X, from.Y], [to.X, to.Y]];
        return new CableRouteLeg("new", null, construction, Length(line), line, to.Site);
    }

    /// <summary>
    /// The segment followed from <paramref name="fromSite"/>, in a tube: one the cable already lies in when the same duct
    /// runs on (a duct spans several segments), otherwise the first empty one nobody else in the view has taken.
    /// </summary>
    private async Task<CableRouteLeg> FollowAsync((long Id, double[][] Coords, Place A, Place B) segment, long fromSite, HashSet<long> taken,
        HashSet<long> mine, List<string> warnings, CancellationToken ct)
    {
        var (id, coords, a, _) = segment;
        var line = fromSite == a.Site ? coords : [.. coords.Reverse()];
        string code = "", construction = "existing";
        int tubes = 0, free = 0;
        long? subduct = null, kept = null;
        await using (var cmd = db.CreateCommand("""
            SELECT r.code, r.construction, s.id, s.occupancy
            FROM route_segment r
            LEFT JOIN duct_segment ds ON ds.route_segment_id = r.id
            LEFT JOIN subduct s ON s.duct_id = ds.duct_id
            WHERE r.id = $1
            ORDER BY s.duct_id, s.number
            """))
        {
            cmd.Parameters.Add(new() { Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                (code, construction) = (reader.GetString(0), reader.GetString(1));
                if (reader.IsDBNull(2))
                {
                    continue;
                }
                tubes++;
                var tube = reader.GetInt64(2);
                if (mine.Contains(tube))
                {
                    kept ??= tube;
                }
                else if (reader.GetString(3) == "empty" && !taken.Contains(tube))
                {
                    free++;
                    subduct ??= tube;
                }
            }
        }
        subduct = kept ?? subduct;
        if (subduct is { } chosen)
        {
            mine.Add(chosen);
        }
        else
        {
            warnings.Add(tubes == 0
                ? $"{code} har inga dukter: kabeln ligger fritt i trasén, eller dra en ny sträcka bredvid."
                : $"{code}: alla {tubes} rör är upptagna. Dra en ny sträcka bredvid, eller välj en annan väg.");
        }
        var to = fromSite == a.Site ? segment.B.Site : a.Site;
        return new CableRouteLeg("segment", new ObjectRef("route-segment", id, code), construction, Length(line), line, to, free, tubes, subduct);
    }

    /// <summary>Tubes earlier cables in the plan's view have been given: they are not empty there.</summary>
    private static Task<HashSet<long>> TakenAsync(PlanView view, long? except, CancellationToken ct)
    {
        _ = ct;
        var taken = new HashSet<long>();
        foreach (var op in view.Chain.Operations.Where(o => o.Kind == "create_cable" && o.Id != except && o.Payload.TryGetProperty("route", out _)))
        {
            foreach (var leg in op.Payload.GetProperty("route").EnumerateArray())
            {
                if (leg.TryGetProperty("subduct", out var s) && s.ValueKind == JsonValueKind.Number)
                {
                    taken.Add(s.GetInt64());
                }
            }
        }
        return Task.FromResult(taken);
    }

    private async Task<Place?> SiteAsync(UserScope scope, PlanView view, long id, CancellationToken ct)
    {
        if (id < 0)
        {
            var op = view.Chain.Operations.FirstOrDefault(o => o.Kind == "create_site" && Planned.ObjectId(o.Id) == id);
            return op is null ? null
                : new Place(id, op.Payload.GetProperty("code").GetString(), op.Payload.GetProperty("x").GetDouble(), op.Payload.GetProperty("y").GetDouble());
        }
        await using var cmd = db.CreateCommand($"""
            SELECT s.code, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)) FROM site s
            WHERE s.id = $1 AND {ScopeSql.Site("s.id", 2)}
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new Place(id, reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)) : null;
    }

    private async Task<(Place A, Place B, double[][] Coords)?> SegmentAsync(UserScope scope, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT r.a_site_id, a.code, ST_X(ST_PointOnSurface(a.geom)), ST_Y(ST_PointOnSurface(a.geom)),
                   r.b_site_id, b.code, ST_X(ST_PointOnSurface(b.geom)), ST_Y(ST_PointOnSurface(b.geom)),
                   ARRAY(SELECT ARRAY[ST_X(p.geom), ST_Y(p.geom)] FROM ST_DumpPoints(r.geom) p ORDER BY p.path)
            FROM route_segment r JOIN site a ON a.id = r.a_site_id JOIN site b ON b.id = r.b_site_id
            WHERE r.id = $1 AND {ScopeSql.RouteSegment("r.id", 2)}
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var points = (double[,])reader.GetValue(8);
        var coords = Enumerable.Range(0, points.GetLength(0)).Select(i => new[] { points[i, 0], points[i, 1] }).ToArray();
        return (new Place(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3)),
                new Place(reader.GetInt64(4), reader.GetString(5), reader.GetDouble(6), reader.GetDouble(7)), coords);
    }

    private async Task<(long Id, double[][] Coords, Place A, Place B)?> DirectSegmentAsync(UserScope scope, long from, long to, CancellationToken ct)
    {
        if (from < 0 || to < 0)
        {
            return null;
        }
        long? id;
        await using (var cmd = db.CreateCommand($"""
            SELECT r.id FROM route_segment r
            WHERE ((r.a_site_id = $1 AND r.b_site_id = $2) OR (r.a_site_id = $2 AND r.b_site_id = $1)) AND {ScopeSql.RouteSegment("r.id", 3)}
            ORDER BY r.id LIMIT 1
            """))
        {
            cmd.Parameters.Add(new() { Value = from });
            cmd.Parameters.Add(new() { Value = to });
            cmd.Parameters.Add(scope.Parameter());
            id = await cmd.ExecuteScalarAsync(ct) as long?;
        }
        if (id is not { } found || await SegmentAsync(scope, found, ct) is not { } segment)
        {
            return null;
        }
        return (found, segment.Coords, segment.A, segment.B);
    }

    private static double Distance(Place a, Place b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double Length(double[][] line) =>
        Math.Round(line.Zip(line.Skip(1), (p, q) => Math.Sqrt(((p[0] - q[0]) * (p[0] - q[0])) + ((p[1] - q[1]) * (p[1] - q[1])))).Sum(), 1);

    /// <summary>The legs as one line from the start, without repeating the point where one leg ends and the next begins.</summary>
    private static double[][] Join(Place start, List<CableRouteLeg> legs) =>
        legs.Count == 0 ? [[start.X, start.Y]] : Join(legs);

    private static double[][] Join(List<CableRouteLeg> legs)
    {
        var line = new List<double[]>();
        foreach (var leg in legs)
        {
            foreach (var p in leg.Coordinates)
            {
                if (line.Count == 0 || Math.Abs(line[^1][0] - p[0]) > 0.01 || Math.Abs(line[^1][1] - p[1]) > 0.01)
                {
                    line.Add(p);
                }
            }
        }
        return [.. line];
    }
}

/// <summary>
/// The drawing's live preview (#259): the line so far, its length in existing conduit and new, the tubes it would take
/// and warnings for full ones. Nothing is stored; the cable is added with the same steps in <c>route</c>.
/// </summary>
public sealed class CableRouteEndpoint(GraphHolder holder, PlanViews views, CableRoutes routes) : Endpoint<CableRouteRequest, CableRoute>
{
    public override void Configure() => Post("/plans/{id}/cable-route");

    public override async Task HandleAsync(CableRouteRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (await views.GetAsync(holder.Require(), req.Id, scope, ct) is not { } view)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var (route, error) = await routes.ResolveAsync(scope, view, req.ASiteId, req.Steps, ct);
        if (route is null)
        {
            await Send.ResultAsync(TypedResults.Problem(error, statusCode: StatusCodes.Status400BadRequest));
            return;
        }
        await Send.OkAsync(route, ct);
    }
}

public sealed class CableRedrawRequest
{
    public long Id { get; set; }
    public long OperationId { get; set; }
    public CableRouteStep[] Route { get; set; } = [];
}

/// <summary>
/// Draws a planned cable again (#259): break points moved or taken away, other segments followed. The operation keeps its
/// place and its planned id, so what refers to the cable (its termination) still does; it must still end at its B site.
/// </summary>
public sealed class CableRedrawEndpoint(RequestDb db, GraphHolder holder, PlanViews views, CableRoutes routes)
    : Endpoint<CableRedrawRequest, CableRoute>
{
    public override void Configure()
    {
        Put("/plans/{id}/operations/{operationId}/route");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(CableRedrawRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (await views.GetAsync(holder.Require(), req.Id, scope, ct) is not { } view
            || view.Chain.Operations.FirstOrDefault(o => o.Id == req.OperationId && o.PlanId == req.Id && o.Kind == "create_cable") is not { } op)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (view.Chain.Plan.Status != "draft")
        {
            await PlanSql.ConflictAsync(HttpContext, "Bara utkast kan ändras.", ct);
            return;
        }
        if (req.Route.Length is 0 or > CableRoutes.MaxSteps)
        {
            await Send.ResultAsync(TypedResults.Problem($"route is 1–{CableRoutes.MaxSteps} steps.", statusCode: StatusCodes.Status400BadRequest));
            return;
        }
        var (a, b) = (op.Payload.GetProperty("a").GetInt64(), op.Payload.GetProperty("b").GetInt64());
        var (route, error) = await routes.ResolveAsync(scope, view, a, req.Route, ct, except: op.Id);
        if (route is null || !route.Complete || route.End!.Id != b)
        {
            await Send.ResultAsync(TypedResults.Problem(error ?? "Kabeln ska sluta på siten i B-änden.", statusCode: StatusCodes.Status400BadRequest));
            return;
        }
        var payload = System.Text.Json.Nodes.JsonNode.Parse(op.Payload.GetRawText())!.AsObject();
        CableRoutes.Write(payload, a, route, req.Route);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("UPDATE plan_operation SET payload = $3::jsonb WHERE plan_id = $1 AND id = $2", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            cmd.Parameters.Add(new() { Value = op.Id });
            cmd.Parameters.Add(new() { Value = payload.ToJsonString() });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await PlanSql.TouchAsync(conn, tx, req.Id, ct);
        await tx.CommitAsync(ct);
        await Send.OkAsync(route, ct);
    }
}

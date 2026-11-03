using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Trace;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Voice;

public sealed record MapPoint(long Id, string Code, string Name, double X, double Y);

/// <param name="Reference">What failed; with <paramref name="ConversationId"/> it ties the incident to the call that created it (#163).</param>
public sealed record MapIncident(string Number, string Priority, MapPoint Site, DateTimeOffset CreatedAt, string Reference, string ConversationId);

/// <param name="Down">Sites and cables of the services that lose every path.</param>
/// <param name="FalseRedundancy">Sites and cables of the services whose backup runs through the fault too.</param>
public sealed record LiveImpact(string Priority, int Affected, int ServicesDown, TraceRoute Down, TraceRoute FalseRedundancy);

/// <summary>What a voice call is looking at right now: the latest tool call that named an object (#156).</summary>
public sealed record LiveFocus(string Tool, string Reference, string ConversationId, DateTimeOffset At, MapPoint Site, LiveImpact? Impact);

public sealed record OperationsLive(IReadOnlyList<MapIncident> Incidents, LiveFocus? Live);

/// <param name="RouteSegments">Route segments the dig crosses (#237), within the caller's scopes; up to 20.</param>
public sealed record MapWork(long Id, string Title, string Contractor, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Ongoing, double[][] Ring,
    IReadOnlyList<Cmdb.Api.Features.Objects.ObjectRef>? RouteSegments = null);

public sealed record MapRisk(string Id, string Kind, string Title, MapPoint Site);

public sealed record OperationsWorks(IReadOnlyList<MapWork> Works, IReadOnlyList<MapRisk> Risks);

/// <summary>
/// The map's operations layer, fast part (#156): open incidents, and the object the latest voice call is about with
/// what its fault takes down, so the map can follow a call while it happens. Polled every few seconds while the agent
/// panel is open. Within the caller's access scopes, like the rest of the map.
/// </summary>
public sealed class OperationsLiveEndpoint(GraphHolder holder, RequestDb db, SystemDb system, ScopeMasks masks) : EndpointWithoutRequest<OperationsLive>
{
    /// <summary>The impact of the latest focus, per graph: recomputed only when the call moves on or the network changes.</summary>
    private static volatile ImpactCache? cache;

    private sealed record ImpactCache(string Key, LiveImpact Impact);

    private static readonly HashSet<string> ImpactTools = new(StringComparer.Ordinal) { "fault_impact", "create_incident", "risk_details" };

    public override void Configure() => Get("/operations/live");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var incidents = new List<MapIncident>();
        await using (var cmd = db.Source.CreateCommand($"""
            SELECT i.id, i.priority, s.id, s.code, s.name, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)), i.created_at,
                   i.reference, i.conversation_id
            FROM incident i JOIN site s ON s.id = i.site_id
            WHERE i.status = 'open' AND {ScopeSql.Site("i.site_id", 1)}
            ORDER BY i.id DESC LIMIT 100
            """))
        {
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                incidents.Add(new MapIncident(Incidents.Number(reader.GetInt64(0)), reader.GetString(1),
                    new MapPoint(reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetDouble(5), reader.GetDouble(6)),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetString(9)));
            }
        }
        if (scope.HidesCoordinates)
        {
            await Send.OkAsync(new OperationsLive([], null), ct);
            return;
        }
        await Send.OkAsync(new OperationsLive(incidents, await LiveAsync(scope, ct)), ct);
    }

    private async Task<LiveFocus?> LiveAsync(UserScope scope, CancellationToken ct)
    {
        if (await LiveCalls.LatestAsync(system.Source, null, ct) is not { } call || holder.Current is not { } graph)
        {
            return null;
        }
        var mask = await masks.GetAsync(graph, scope, ct);
        if (await LiveCalls.InScopeAsync(graph, mask, db.Source, call, scope, ct) is not { } focus)
        {
            // Outside the caller's scope: the call is not theirs to follow.
            return null;
        }
        var (fault, site) = focus;
        LiveImpact? impact = null;
        if (ImpactTools.Contains(call.Tool))
        {
            var key = $"{call.Reference}|{scope.Signature}|{scope.Unrestricted}|{scope.Clips}|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(graph)}";
            if (cache is { } hit && hit.Key == key)
            {
                impact = hit.Impact;
            }
            else
            {
                impact = await OperationsImpact.RoutesAsync(graph, mask, db.Source, fault, scope, ct);
                cache = new ImpactCache(key, impact);
            }
        }
        return new LiveFocus(call.Tool, call.Reference, call.ConversationId, call.At, site, impact);
    }
}

public sealed class LiveCallRequest
{
    public string Conversation { get; set; } = "";
}

/// <summary>
/// Marks a call as over, so the map stops following it for everyone (#328): nothing else tells the server that a call
/// has ended. People with write access only, and only a call they can see. Ending one that is already over changes nothing.
/// </summary>
public sealed class EndLiveCallEndpoint(GraphHolder holder, RequestDb db, SystemDb system, ScopeMasks masks) : Endpoint<LiveCallRequest>
{
    public override void Configure()
    {
        Post("/operations/live/{conversation}/end");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(LiveCallRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (await LiveCalls.LatestAsync(system.Source, req.Conversation, ct) is { } call)
        {
            if (holder.Current is not { } graph
                || await LiveCalls.InScopeAsync(graph, await masks.GetAsync(graph, scope, ct), db.Source, call, scope, ct) is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            await LiveCalls.EndAsync(system.Source, call.ConversationId, User.FindFirstValue(CmdbClaims.Username) ?? User.Identity?.Name ?? "?", ct);
        }
        else if (!await LiveCalls.EndedAsync(system.Source, req.Conversation, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.NoContentAsync(ct);
    }
}

/// <summary>The latest tool call of a call the map may follow, shared by the live focus and ending a call (#328).</summary>
internal sealed record LiveCall(string Tool, string Reference, string ConversationId, DateTimeOffset At);

internal static class LiveCalls
{
    /// <summary>A call lasts at most ten minutes (agent/deploy.py), so a tool call older than that belongs to a call that is over.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The latest tool call that named an object, in calls nobody has ended since, optionally of one conversation.</summary>
    public static async Task<LiveCall?> LatestAsync(NpgsqlDataSource system, string? conversation, CancellationToken ct)
    {
        await using var cmd = system.CreateCommand("""
            SELECT c.tool, c.reference, c.conversation_id, c.created_at
            FROM voice_tool_call c LEFT JOIN voice_call_end e ON e.conversation_id = c.conversation_id
            WHERE c.reference IS NOT NULL AND c.outcome NOT IN ('refused', 'error') AND c.created_at > now() - $1
              AND (e.ended_at IS NULL OR c.created_at > e.ended_at)
              AND ($2::text IS NULL OR c.conversation_id = $2)
            ORDER BY c.id DESC LIMIT 1
            """);
        cmd.Parameters.Add(new() { Value = Window });
        cmd.Parameters.Add(new() { Value = (object?)conversation ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new LiveCall(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    /// <summary>What the call is about and where, or null when that is outside the caller's scope.</summary>
    public static async Task<(Fault Fault, MapPoint Site)?> InScopeAsync(Cmdb.Graph.Graph graph, GraphMask mask, NpgsqlDataSource db, LiveCall call, UserScope scope,
        CancellationToken ct)
    {
        if (OperationsImpact.Parse(call.Reference) is not { } target
            || await FaultAnalysis.RunAsync(graph, mask, db, target.Type, target.Id, ct) is not { } fault)
        {
            return null;
        }
        await using var cmd = db.CreateCommand($"""
            SELECT id, code, name, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site
            WHERE id = $2 AND {ScopeSql.Site("id", 1)}
            """);
        cmd.Parameters.Add(scope.Parameter());
        cmd.Parameters.Add(new() { Value = fault.SiteId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (fault, new MapPoint(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4)))
            : null;
    }

    public static async Task EndAsync(NpgsqlDataSource system, string conversation, string by, CancellationToken ct)
    {
        await using var cmd = system.CreateCommand("""
            INSERT INTO voice_call_end (conversation_id, ended_by) VALUES ($1, $2)
            ON CONFLICT (conversation_id) DO UPDATE SET ended_by = excluded.ended_by, ended_at = now()
            """);
        cmd.Parameters.Add(new() { Value = conversation });
        cmd.Parameters.Add(new() { Value = by });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<bool> EndedAsync(NpgsqlDataSource system, string conversation, CancellationToken ct)
    {
        await using var cmd = system.CreateCommand("SELECT EXISTS (SELECT 1 FROM voice_call_end WHERE conversation_id = $1)");
        cmd.Parameters.Add(new() { Value = conversation });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>What a fault takes down, as map routes (#156, #161): shared by the live call and each incident.</summary>
public static class OperationsImpact
{
    /// <summary>The routes of the services the fault takes down: at most a dozen of each kind, enough to see and quick to draw.</summary>
    public static async Task<LiveImpact> RoutesAsync(Cmdb.Graph.Graph graph, GraphMask mask, NpgsqlDataSource db, Fault fault, UserScope scope,
        CancellationToken ct)
    {
        async Task<TraceRoute> RoutesAsync(Redundancy kind)
        {
            var sites = new Dictionary<long, ObjectRef>();
            var cables = new Dictionary<long, ObjectRef>();
            foreach (var service in fault.Services.Where(s => s.Redundancy == kind).Take(12))
            {
                if (await TraceEndpoint.RunAsync(graph, mask, db, null, service.Id, null, ct) is not { } trace)
                {
                    continue;
                }
                foreach (var s in trace.Sites)
                {
                    sites.TryAdd(s.Id, s);
                }
                foreach (var c in trace.Cables)
                {
                    cables.TryAdd(c.Id, c);
                }
            }
            return await TraceEndpoint.RouteAsync(db, [.. sites.Values], [.. cables.Values], scope, ct);
        }
        return new LiveImpact(fault.Priority, fault.Affected, fault.Down, await RoutesAsync(Redundancy.None), await RoutesAsync(Redundancy.False));
    }

    public static (string Type, long Id)? Parse(string reference)
    {
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && reference[..colon] is "site" or "equipment" or "cable"
            && long.TryParse(reference.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? (reference[..colon], id)
            : null;
    }
}

/// <summary>
/// The map's operations layer, slow part (#156): planned and ongoing work areas that cross a cable in the caller's
/// scope, and the risks the proactive agent has found. Polled every half minute: the risks cost an impact analysis each.
/// </summary>
public sealed class OperationsWorksEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks, Classifications.ClassificationRules rules) : EndpointWithoutRequest<OperationsWorks>
{
    public override void Configure() => Get("/operations/works");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (scope.HidesCoordinates || holder.Current is not { } graph)
        {
            await Send.OkAsync(new OperationsWorks([], []), ct);
            return;
        }
        var works = new List<MapWork>();
        await using (var cmd = db.Source.CreateCommand($"""
            SELECT w.id, w.title, w.contractor, w.starts_at, w.ends_at,
                   ARRAY(SELECT ARRAY[round(ST_X(p.geom)), round(ST_Y(p.geom))] FROM ST_DumpPoints(ST_ExteriorRing(w.area)) p ORDER BY p.path)
            FROM planned_work w
            WHERE w.ends_at > now()
              AND EXISTS (SELECT 1 FROM cable c WHERE c.geom && w.area AND ST_Intersects(c.geom, w.area) AND {ScopeSql.Cable("c.id", 1)})
            ORDER BY w.starts_at
            """))
        {
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var from = reader.GetFieldValue<DateTimeOffset>(3);
                var coordinates = (double[,])reader.GetValue(5);
                works.Add(new MapWork(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), from, reader.GetFieldValue<DateTimeOffset>(4),
                    from <= DateTimeOffset.UtcNow,
                    [.. Enumerable.Range(0, coordinates.GetLength(0)).Select(i => new[] { coordinates[i, 0], coordinates[i, 1] })]));
            }
        }
        // The route segments each dig crosses (#237): all the cables in their ducts are at risk.
        if (works.Count > 0)
        {
            var crossed = new Dictionary<long, List<Cmdb.Api.Features.Objects.ObjectRef>>();
            await using var cmd = db.Source.CreateCommand($"""
                SELECT w.id, r.id, r.code, r.lifecycle::text
                FROM planned_work w CROSS JOIN LATERAL (
                    SELECT r.id, r.code, r.lifecycle FROM route_segment r
                    WHERE r.geom && w.area AND ST_Intersects(r.geom, w.area) AND {ScopeSql.RouteSegment("r.id", 2)}
                    ORDER BY r.code LIMIT 20) r
                WHERE w.id = ANY($1)
                """);
            cmd.Parameters.Add(new() { Value = works.Select(w => w.Id).ToArray() });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!crossed.TryGetValue(reader.GetInt64(0), out var list))
                {
                    crossed[reader.GetInt64(0)] = list = [];
                }
                list.Add(new Cmdb.Api.Features.Objects.ObjectRef("route-segment", reader.GetInt64(1), reader.GetString(2), null, reader.GetString(3)));
            }
            works = [.. works.Select(w => crossed.TryGetValue(w.Id, out var segments) ? w with { RouteSegments = segments } : w)];
        }
        var risks = await RiskDetection.RunAsync(graph, await masks.GetAsync(graph, scope, ct), db.Source, scope, ct, rules);
        var sites = new Dictionary<long, MapPoint>();
        await using (var cmd = db.Source.CreateCommand("SELECT id, code, name, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site WHERE id = ANY($1)"))
        {
            cmd.Parameters.Add(new() { Value = risks.Select(r => r.SiteId).Distinct().ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sites[reader.GetInt64(0)] = new MapPoint(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4));
            }
        }
        await Send.OkAsync(new OperationsWorks(works,
            [.. risks.Where(r => sites.ContainsKey(r.SiteId)).Select(r => new MapRisk(r.Id, r.Kind, r.Title, sites[r.SiteId]))]), ct);
    }
}

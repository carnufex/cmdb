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

public sealed record MapWork(long Id, string Title, string Contractor, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Ongoing, double[][] Ring);

public sealed record MapRisk(string Id, string Kind, string Title, MapPoint Site);

public sealed record OperationsWorks(IReadOnlyList<MapWork> Works, IReadOnlyList<MapRisk> Risks);

/// <summary>
/// The map's operations layer, fast part (#156): open incidents, and the object the latest voice call is about with
/// what its fault takes down, so the map can follow a call while it happens. Polled every few seconds while the agent
/// panel is open. Within the caller's access scopes, like the rest of the map.
/// </summary>
public sealed class OperationsLiveEndpoint(GraphHolder holder, RequestDb db, SystemDb system, ScopeMasks masks) : EndpointWithoutRequest<OperationsLive>
{
    /// <summary>A call older than this is no longer live.</summary>
    private static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(15);

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
        string tool, reference, conversation;
        DateTimeOffset at;
        await using (var cmd = system.Source.CreateCommand("""
            SELECT tool, reference, conversation_id, created_at FROM voice_tool_call
            WHERE reference IS NOT NULL AND outcome NOT IN ('refused', 'error') AND created_at > now() - $1
            ORDER BY id DESC LIMIT 1
            """))
        {
            cmd.Parameters.Add(new() { Value = LiveWindow });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            (tool, reference, conversation, at) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
        }
        if (holder.Current is not { } graph || OperationsImpact.Parse(reference) is not { } target)
        {
            return null;
        }
        var mask = await masks.GetAsync(graph, scope, ct);
        var fault = await FaultAnalysis.RunAsync(graph, mask, db.Source, target.Type, target.Id, ct);
        if (fault is null || await SiteAsync(fault.SiteId, scope, ct) is not { } site)
        {
            // Outside the caller's scope: the call is not theirs to follow.
            return null;
        }
        LiveImpact? impact = null;
        if (ImpactTools.Contains(tool))
        {
            var key = $"{reference}|{scope.Signature}|{scope.Unrestricted}|{scope.Clips}|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(graph)}";
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
        return new LiveFocus(tool, reference, conversation, at, site, impact);
    }

    private async Task<MapPoint?> SiteAsync(long id, UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand($"""
            SELECT id, code, name, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site
            WHERE id = $2 AND {ScopeSql.Site("id", 1)}
            """);
        cmd.Parameters.Add(scope.Parameter());
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new MapPoint(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4))
            : null;
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
public sealed class OperationsWorksEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks) : EndpointWithoutRequest<OperationsWorks>
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
        var risks = await RiskDetection.RunAsync(graph, await masks.GetAsync(graph, scope, ct), db.Source, scope, ct);
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

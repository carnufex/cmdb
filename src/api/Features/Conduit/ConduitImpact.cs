using System.Diagnostics;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Conduit;

/// <summary>
/// Route segment → cables (ADR-0014 §7, #237): which cables lie in a segment's ducts, as graph cable indexes, built from
/// the conduit for each graph version. A new graph (a load, or a change from the change stream, #11) gives a new version,
/// so the index follows the graph. The conduit is written only by loads today; when plans write it, its tables join the
/// change stream.
/// </summary>
public sealed class ConduitIndex(SystemDb db) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Version, IReadOnlyDictionary<long, int[]> Cables)? _current;

    public void Dispose() => _lock.Dispose();

    /// <summary>The cables of every route segment, for this graph.</summary>
    public async Task<IReadOnlyDictionary<long, int[]>> GetAsync(Cmdb.Graph.Graph graph, CancellationToken ct)
    {
        if (_current is { } current && current.Version == graph.Version)
        {
            return current.Cables;
        }
        await _lock.WaitAsync(ct);
        try
        {
            if (_current is { } again && again.Version == graph.Version)
            {
                return again.Cables;
            }
            var cables = new Dictionary<long, List<int>>();
            await using var cmd = db.Source.CreateCommand("""
                SELECT ds.route_segment_id, cp.cable_id
                FROM cable_path cp JOIN subduct s ON s.id = cp.subduct_id JOIN duct_segment ds ON ds.duct_id = s.duct_id
                """);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (graph.TryGetCable(reader.GetInt64(1), out var cable))
                {
                    if (!cables.TryGetValue(reader.GetInt64(0), out var list))
                    {
                        cables[reader.GetInt64(0)] = list = [];
                    }
                    list.Add(cable);
                }
            }
            var index = cables.ToDictionary(c => c.Key, c => c.Value.Distinct().ToArray());
            _current = (graph.Version, index);
            return index;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Impact of a dig across a route segment: every cable in its ducts is cut, with the same walk as one cable (#10).
    /// Cables outside the caller's scope count for what they carry, as far as the caller may see it, and are only counted.
    /// </summary>
    internal async Task<Impact?> RunAsync(Cmdb.Graph.Graph g, GraphMask mask, RequestDb requestDb, UserScope scope, long segment, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using (var cmd = requestDb.CreateCommand($"SELECT EXISTS (SELECT 1 FROM route_segment r WHERE r.id = $1 AND {ScopeSql.RouteSegment("r.id", 2)})"))
        {
            cmd.Parameters.Add(new() { Value = segment });
            cmd.Parameters.Add(scope.Parameter());
            if (!(bool)(await cmd.ExecuteScalarAsync(ct))!)
            {
                return null;
            }
        }
        var cables = (await GetAsync(g, ct)).GetValueOrDefault(segment) ?? [];
        var nodes = new List<int>();
        foreach (var cable in cables)
        {
            nodes.AddRange(g.EndsOf(cable).ToArray());
        }
        var impact = await ImpactEndpoint.FromResultAsync(g, mask, requestDb, GraphImpact.OfNodes(g, [.. nodes]), sw, ct);
        var visible = cables.Where(mask.CableVisible).Select(g.CableId).ToArray();
        var names = new List<ObjectRef>();
        await using (var cmd = requestDb.CreateCommand("SELECT id, code, lifecycle::text FROM cable WHERE id = ANY($1) ORDER BY code"))
        {
            cmd.Parameters.Add(new() { Value = visible });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                names.Add(new ObjectRef("cable", reader.GetInt64(0), reader.GetString(1), null, reader.GetString(2)));
            }
        }
        return impact with { Cables = names, HiddenCables = cables.Length - visible.Length, ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2) };
    }
}

/// <summary>What a dig across a route segment would affect (#237): its cables, their circuits and the services on them.</summary>
public sealed class RouteSegmentImpactEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks, ConduitIndex index) : Endpoint<RouteSegmentRequest, Impact>
{
    public override void Configure() => Get("/route-segments/{id}/impact");

    public override async Task HandleAsync(RouteSegmentRequest req, CancellationToken ct)
    {
        if (holder.Current is not { } graph)
        {
            await Send.ResultAsync(TypedResults.Problem("The network graph is still loading; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable));
            return;
        }
        var scope = HttpContext.Scope();
        if (await index.RunAsync(graph, await masks.GetAsync(graph, scope, ct), db, scope, req.Id, ct) is not { } impact)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(impact, ct);
    }
}

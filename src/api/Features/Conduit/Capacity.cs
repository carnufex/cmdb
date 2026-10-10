using System.ComponentModel;
using System.Diagnostics;
using Cmdb.Api.Agents;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Graph;
using FastEndpoints;
using ModelContextProtocol.Server;

namespace Cmdb.Api.Features.Conduit;

/// <param name="Usage">lit (in a circuit in service), dark, dark_fibre (leased) or spare (ADR-0014).</param>
/// <param name="Stated">The usage someone stated, when it is not derived.</param>
public sealed record ConductorUsage(long Id, int Number, string? Color, string Usage, string? Stated, IReadOnlyList<ObjectRef> Circuits);

public sealed record CableConductorsRequest(long Id);

public sealed record CapacitySegment(long Id, string Code, string Construction, int FreeTubes, int Tubes, double X, double Y);

public sealed record CapacityCable(long Id, string Code, int FreeFibres, int Fibres);

public sealed record CapacityResult(IReadOnlyList<CapacitySegment> Segments, IReadOnlyList<CapacityCable> Cables, bool Truncated, double ElapsedMs);

public sealed class CapacityRequest
{
    /// <summary>Route segments with at least this many empty tubes; 0 leaves them out.</summary>
    public int MinFreeTubes { get; set; }

    /// <summary>Cables with at least this many free fibres (not lit, not leased, not spare); 0 leaves them out.</summary>
    public int MinFreeFibres { get; set; }

    public int Limit { get; set; } = 100;
}

/// <summary>
/// Each conductor of a cable with its usage (ADR-0014, #238): lit when it is in a circuit in service, otherwise what someone
/// stated (dark, leased dark fibre, spare), or dark. Circuits outside the caller's scope are not named.
/// </summary>
public sealed class CableConductorsEndpoint(RequestDb db) : Endpoint<CableConductorsRequest, IReadOnlyList<ConductorUsage>>
{
    public override void Configure() => Get("/cables/{id}/conductors");

    public override async Task HandleAsync(CableConductorsRequest req, CancellationToken ct)
    {
        var list = await LoadAsync(db, req.Id, HttpContext.Scope(), ct);
        if (list is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(list, ct);
    }

    internal static async Task<IReadOnlyList<ConductorUsage>?> LoadAsync(RequestDb db, long cable, UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT k.id, k.number, k.color, k.usage,
                   coalesce((SELECT array_agg(DISTINCT r.id ORDER BY r.id) FROM conductor_end e JOIN circuit_hop h ON h.terminal_id = e.terminal_id
                             JOIN circuit r ON r.id = h.circuit_id AND r.lifecycle = 'in_service' WHERE e.conductor_id = k.id), ARRAY[]::bigint[]),
                   EXISTS (SELECT 1 FROM cable c WHERE c.id = $1 AND {ScopeSql.Cable("c.id", 2)})
            FROM conductor k WHERE k.cable_id = $1 ORDER BY k.number
            """);
        cmd.Parameters.Add(new() { Value = cable });
        cmd.Parameters.Add(scope.Parameter());
        var rows = new List<(long Id, int Number, string? Color, string? Stated, long[] Circuits)>();
        var visible = false;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                visible = reader.GetBoolean(5);
                rows.Add((reader.GetInt64(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<long[]>(4)));
            }
        }
        if (!visible)
        {
            return null;
        }
        var names = new Dictionary<long, ObjectRef>();
        await using (var circuits = db.CreateCommand($"SELECT r.id, r.code, r.lifecycle::text FROM circuit r WHERE r.id = ANY($1) AND {ScopeSql.Circuit("r.id", 2)}"))
        {
            circuits.Parameters.Add(new() { Value = rows.SelectMany(r => r.Circuits).Distinct().ToArray() });
            circuits.Parameters.Add(scope.Parameter());
            await using var reader = await circuits.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                names[reader.GetInt64(0)] = new ObjectRef("circuit", reader.GetInt64(0), reader.GetString(1), null, reader.GetString(2));
            }
        }
        return [.. rows.Select(r => new ConductorUsage(r.Id, r.Number, r.Color, r.Circuits.Length > 0 ? "lit" : r.Stated ?? "dark", r.Stated,
            [.. r.Circuits.Select(c => names.GetValueOrDefault(c) ?? ObjectRef.Hidden("circuit"))]))];
    }
}

/// <summary>
/// Free capacity (ADR-0014, #238): route segments with empty tubes and cables with free fibres, within the caller's scopes.
/// A fibre is free when no circuit in service runs on it and nobody stated it is leased or spare; that is worked out in the
/// graph in memory, so the whole network answers in milliseconds.
/// </summary>
public sealed class CapacityEndpoint(RequestDb db, GraphHolder holder, ScopeMasks masks) : Endpoint<CapacityRequest, CapacityResult>
{
    public const int MaxLimit = 500;

    public override void Configure() => Get("/conduit/capacity");

    public override async Task HandleAsync(CapacityRequest req, CancellationToken ct) =>
        await Send.OkAsync(await RunAsync(db, holder, masks, HttpContext.Scope(), req, ct), ct);

    internal static async Task<CapacityResult> RunAsync(RequestDb db, GraphHolder holder, ScopeMasks masks, UserScope scope, CapacityRequest req,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var limit = Math.Clamp(req.Limit, 1, MaxLimit);
        var truncated = false;
        var segments = new List<CapacitySegment>();
        if (req.MinFreeTubes > 0 && !scope.HidesCoordinates)
        {
            await using var cmd = db.CreateCommand($"""
                WITH free AS (
                    SELECT ds.route_segment_id AS id, count(*) FILTER (WHERE s.occupancy = 'empty' AND NOT EXISTS (
                               SELECT 1 FROM reservation v WHERE v.resource_kind = 'subduct' AND v.resource_id = s.id AND v.released_at IS NULL))::int AS free,
                           count(*)::int AS tubes
                    FROM duct_segment ds JOIN subduct s ON s.duct_id = ds.duct_id GROUP BY ds.route_segment_id
                )
                SELECT r.id, r.code, r.construction, f.free, f.tubes, ST_X(ST_LineInterpolatePoint(r.geom, 0.5)), ST_Y(ST_LineInterpolatePoint(r.geom, 0.5))
                FROM free f JOIN route_segment r ON r.id = f.id
                WHERE f.free >= $1 AND r.lifecycle <> 'removed' AND {ScopeSql.RouteSegment("r.id", 2)}
                ORDER BY f.free DESC, r.code LIMIT $3
                """);
            cmd.Parameters.Add(new() { Value = req.MinFreeTubes });
            cmd.Parameters.Add(scope.Parameter());
            cmd.Parameters.Add(new() { Value = limit + 1 });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                segments.Add(new CapacitySegment(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                    Math.Round(reader.GetDouble(5)), Math.Round(reader.GetDouble(6))));
            }
            truncated |= segments.Count > limit;
            segments = [.. segments.Take(limit)];
        }

        var cables = new List<CapacityCable>();
        if (req.MinFreeFibres > 0 && holder.Current is { } graph)
        {
            // Stated usage that takes a fibre out of the free pool, per cable.
            var stated = new Dictionary<long, HashSet<int>>();
            await using (var cmd = db.CreateCommand("SELECT cable_id, number FROM conductor WHERE usage IN ('dark_fibre', 'spare')"))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (!stated.TryGetValue(reader.GetInt64(0), out var set))
                    {
                        stated[reader.GetInt64(0)] = set = [];
                    }
                    set.Add(reader.GetInt32(1));
                }
            }
            var mask = await masks.GetAsync(graph, scope, ct);
            var found = new List<(long Id, int Free, int Fibres)>();
            for (var c = 0; c < graph.CableCount; c++)
            {
                if (!mask.CableVisible(c))
                {
                    continue;
                }
                // A conductor is its two ends, joined by a conductor edge; it is lit when a circuit runs through either end.
                var ends = graph.EndsOf(c);
                var seen = new HashSet<int>();
                var (fibres, lit) = (0, 0);
                foreach (var end in ends)
                {
                    if (!seen.Add(end))
                    {
                        continue;
                    }
                    var other = -1;
                    var neighbours = graph.Neighbours(end);
                    var kinds = graph.NeighbourKinds(end);
                    for (var n = 0; n < kinds.Length; n++)
                    {
                        if (kinds[n] == EdgeKind.Conductor)
                        {
                            other = neighbours[n];
                            seen.Add(other);
                            break;
                        }
                    }
                    fibres++;
                    if (graph.CircuitsThrough(end).Length > 0 || (other >= 0 && graph.CircuitsThrough(other).Length > 0))
                    {
                        lit++;
                    }
                }
                var id = graph.CableId(c);
                var free = fibres - lit - (stated.TryGetValue(id, out var taken) ? taken.Count : 0);
                if (free >= req.MinFreeFibres)
                {
                    found.Add((id, free, fibres));
                }
            }
            var top = found.OrderByDescending(f => f.Free).ThenBy(f => f.Id).Take(limit + 1).ToList();
            truncated |= top.Count > limit;
            top = [.. top.Take(limit)];
            var codes = new Dictionary<long, string>();
            await using (var cmd = db.CreateCommand("SELECT id, code FROM cable WHERE id = ANY($1)"))
            {
                cmd.Parameters.Add(new() { Value = top.Select(t => t.Id).ToArray() });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    codes[reader.GetInt64(0)] = reader.GetString(1);
                }
            }
            cables = [.. top.Where(t => codes.ContainsKey(t.Id)).Select(t => new CapacityCable(t.Id, codes[t.Id], t.Free, t.Fibres))];
        }
        return new CapacityResult(segments, cables, truncated, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
    }
}

/// <summary>Free capacity for agents (#238): where there are empty tubes and free fibres.</summary>
[McpServerToolType]
public sealed class CapacityTools(RequestDb db, GraphHolder holder, ScopeMasks masks, IHttpContextAccessor http)
{
    [McpServerTool(Name = "find_capacity", Title = "Ledig kapacitet", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Free capacity in the network: route segments (conduit) with at least minFreeTubes empty tubes, and cables with at " +
        "least minFreeFibres free fibres (not in a circuit in service, not leased as dark fibre, not kept spare). Most free first.")]
    public async Task<CapacityResult> FindCapacity(
        [Description("Route segments with at least this many empty tubes; 0 leaves them out.")] int minFreeTubes = 0,
        [Description("Cables with at least this many free fibres; 0 leaves them out.")] int minFreeFibres = 0,
        [Description("At most this many of each, 1–100. Default 25.")] int limit = 25,
        CancellationToken ct = default)
    {
        if (minFreeTubes <= 0 && minFreeFibres <= 0)
        {
            throw new ModelContextProtocol.McpException("Give minFreeTubes or minFreeFibres.");
        }
        return await CapacityEndpoint.RunAsync(db, holder, masks, http.HttpContext!.Scope(),
            new CapacityRequest { MinFreeTubes = minFreeTubes, MinFreeFibres = minFreeFibres, Limit = Math.Clamp(limit, 1, 100) }, ct);
    }
}

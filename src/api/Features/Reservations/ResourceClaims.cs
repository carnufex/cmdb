using Cmdb.Api.Auth;
using Npgsql;

namespace Cmdb.Api.Features.Reservations;

public sealed record PlanRef(long Id, string Name);

/// <param name="Holder">The plan's name or the service's code; "Dold" when the caller cannot see the holder.</param>
public sealed record ReservationRef(long Id, string HolderKind, long HolderId, string Holder, string Reason, string CreatedBy);

/// <summary>Who has a claim on a resource (#25): the reservation holding it, and the draft plans that want it.</summary>
/// <param name="Conflict">
/// Two plans that do not build on each other want it, or a plan wants what someone else has reserved.
/// </param>
public sealed record ResourceClaims(ReservationRef? Reservation, IReadOnlyList<PlanRef> WantedBy, bool Conflict = false)
{
    public static ResourceClaims None { get; } = new(null, []);
}

/// <summary>A conflict for one plan operation: another holder has the resource, or another plan wants it too.</summary>
/// <param name="Blocking">A reservation by someone else blocks applying; another plan wanting the same is a warning.</param>
public sealed record PlanConflict(long OperationId, long PlanId, bool Blocking, string HolderKind, long HolderId, string HolderName)
{
    /// <summary>In words, naming the other plan or service only when the caller can see it (#22).</summary>
    public string Message(UserScope scope, bool serviceVisible = false)
    {
        var name = HolderKind == "plan" ? (scope.SeesPlan(HolderId) ? HolderName : null) : (serviceVisible ? HolderName : null);
        var who = HolderKind == "plan" ? "planen" : "tjänsten";
        return Blocking
            ? (name is null ? $"Reserverad av {(HolderKind == "plan" ? "en annan plan" : "en tjänst")}." : $"Reserverad av {who} {name}.")
            : (name is null ? "Önskas också av en annan plan." : $"Önskas också av planen {name}.");
    }
}

/// <summary>
/// Claims and conflicts on resources (#25). A draft plan wants the terminals its connect operations join, and the
/// conductors of those that are conductor ends; a reservation holds a terminal, conductor, slot or channel. Plans in
/// the same dependency chain build on each other and never conflict.
/// </summary>
internal static class ClaimsSql
{
    // Draft plans' claims: (operation, plan, resource kind, resource id). Planned terminals (negative ids) are left out:
    // their ids come from the plan's own operations, so no other plan can claim them, and an import makes tens of
    // thousands of them (#170).
    private const string ClaimsCte = """
        claims AS (
            SELECT o.id AS op, o.plan_id, 'terminal' AS kind, t.id AS rid
            FROM plan_operation o JOIN plan p ON p.id = o.plan_id AND p.status = 'draft'
            CROSS JOIN LATERAL (VALUES ((o.payload->>'a')::bigint), ((o.payload->>'b')::bigint)) t(id)
            WHERE o.kind = 'connect' AND t.id > 0
            UNION ALL
            SELECT o.id, o.plan_id, 'conductor', ce.conductor_id
            FROM plan_operation o JOIN plan p ON p.id = o.plan_id AND p.status = 'draft'
            CROSS JOIN LATERAL (VALUES ((o.payload->>'a')::bigint), ((o.payload->>'b')::bigint)) t(id)
            JOIN conductor_end ce ON ce.terminal_id = t.id
            WHERE o.kind = 'connect'
        ),
        ancestry AS (
            WITH RECURSIVE up(plan_id, ancestor) AS (
                SELECT plan_id, depends_on_id FROM plan_dependency
                UNION
                SELECT up.plan_id, d.depends_on_id FROM up JOIN plan_dependency d ON d.plan_id = up.ancestor
            )
            SELECT plan_id, ancestor FROM up
        )
        """;

    /// <summary>Whether two plans are the same or one builds on the other.</summary>
    private static string Related(string x, string y) =>
        $"({x} = {y} OR EXISTS (SELECT 1 FROM ancestry anc WHERE (anc.plan_id = {x} AND anc.ancestor = {y}) OR (anc.plan_id = {y} AND anc.ancestor = {x})))";

    /// <summary>Conflicts of the given plans' operations, or of every draft plan when <paramref name="plans"/> is null.</summary>
    public static async Task<List<PlanConflict>> ConflictsAsync(NpgsqlDataSource db, long[]? plans, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            WITH {ClaimsCte}
            SELECT DISTINCT c.op, c.plan_id, 'reserved', r.holder_kind, r.holder_id,
                   CASE r.holder_kind WHEN 'plan' THEN (SELECT name FROM plan WHERE id = r.holder_id)
                                      ELSE (SELECT code FROM service WHERE id = r.holder_id) END
            FROM claims c
            JOIN reservation r ON r.released_at IS NULL AND r.resource_kind = c.kind AND r.resource_id = c.rid
            WHERE ($1::bigint[] IS NULL OR c.plan_id = ANY($1))
              AND NOT (r.holder_kind = 'plan' AND {Related("c.plan_id", "r.holder_id")})
            UNION
            SELECT DISTINCT c.op, c.plan_id, 'wanted', 'plan', q.id, q.name
            FROM claims c
            JOIN claims d ON d.kind = c.kind AND d.rid = c.rid AND d.plan_id <> c.plan_id
            JOIN plan q ON q.id = d.plan_id
            WHERE ($1::bigint[] IS NULL OR c.plan_id = ANY($1))
              AND NOT {Related("c.plan_id", "d.plan_id")}
            ORDER BY 1, 3, 5
            """);
        cmd.Parameters.Add(new() { Value = (object?)plans ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PlanConflict>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PlanConflict(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2) == "reserved", reader.GetString(3),
                reader.GetInt64(4), reader.IsDBNull(5) ? "?" : reader.GetString(5)));
        }
        // A plan holding a reservation on the resource also wants it; saying so twice adds nothing.
        var holds = list.Where(c => c.Blocking).Select(c => (c.OperationId, c.HolderKind, c.HolderId)).ToHashSet();
        return [.. list.Where(c => c.Blocking || !holds.Contains((c.OperationId, c.HolderKind, c.HolderId)))];
    }

    /// <summary>Claims on terminals (ports or conductor ends), with holders the caller cannot see hidden.</summary>
    public static Task<Dictionary<long, ResourceClaims>> ForTerminalsAsync(NpgsqlDataSource db, long[] terminals, UserScope scope, CancellationToken ct) =>
        ForAsync(db, "terminal", terminals, scope, ct);

    /// <summary>Claims on conductors (fibres): reservations on the conductor, and plans connecting either of its ends.</summary>
    public static Task<Dictionary<long, ResourceClaims>> ForConductorsAsync(NpgsqlDataSource db, long[] conductors, UserScope scope, CancellationToken ct) =>
        ForAsync(db, "conductor", conductors, scope, ct);

    private static async Task<Dictionary<long, ResourceClaims>> ForAsync(NpgsqlDataSource db, string kind, long[] ids, UserScope scope,
        CancellationToken ct)
    {
        var result = new Dictionary<long, ResourceClaims>();
        if (ids.Length == 0)
        {
            return result;
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new($"""
                    SELECT r.resource_id, r.id, r.holder_kind, r.holder_id,
                           CASE r.holder_kind WHEN 'plan' THEN (SELECT name FROM plan WHERE id = r.holder_id)
                                              ELSE (SELECT code FROM service s WHERE s.id = r.holder_id AND {ScopeSql.Service("s.id", 3)}) END,
                           r.reason, r.created_by
                    FROM reservation r
                    WHERE r.released_at IS NULL AND r.resource_kind = $1 AND r.resource_id = ANY($2)
                    """) { Parameters = { new() { Value = kind }, new() { Value = ids }, scope.Parameter() } },
                new($"""
                    WITH {ClaimsCte}
                    SELECT DISTINCT c.rid, p.id, p.name FROM claims c JOIN plan p ON p.id = c.plan_id
                    WHERE c.kind = $1 AND c.rid = ANY($2)
                    ORDER BY c.rid, p.id
                    """) { Parameters = { new() { Value = kind }, new() { Value = ids } } },
                // Which plans build on which, to tell a chain from a conflict.
                new($"""
                    WITH {ClaimsCte}
                    SELECT plan_id, ancestor FROM ancestry
                    """),
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);
        var reservations = new Dictionary<long, ReservationRef>();
        var holders = new Dictionary<long, (string Kind, long Id)>();
        while (await reader.ReadAsync(ct))
        {
            var holderKind = reader.GetString(2);
            var holderId = reader.GetInt64(3);
            holders[reader.GetInt64(0)] = (holderKind, holderId);
            var visible = holderKind == "plan" ? scope.SeesPlan(holderId) : !reader.IsDBNull(4);
            reservations[reader.GetInt64(0)] = new ReservationRef(reader.GetInt64(1), holderKind, visible ? holderId : 0,
                visible && !reader.IsDBNull(4) ? reader.GetString(4) : "Dold", visible ? reader.GetString(5) : "", reader.GetString(6));
        }
        await reader.NextResultAsync(ct);
        var wanted = new Dictionary<long, List<PlanRef>>();
        var wanting = new Dictionary<long, List<long>>();
        while (await reader.ReadAsync(ct))
        {
            var (resource, plan) = (reader.GetInt64(0), reader.GetInt64(1));
            if (!wanting.TryGetValue(resource, out var all))
            {
                wanting[resource] = all = [];
            }
            all.Add(plan);
            if (!scope.SeesPlan(plan))
            {
                continue;
            }
            if (!wanted.TryGetValue(resource, out var list))
            {
                wanted[resource] = list = [];
            }
            list.Add(new PlanRef(plan, reader.GetString(2)));
        }
        await reader.NextResultAsync(ct);
        var related = new HashSet<(long, long)>();
        while (await reader.ReadAsync(ct))
        {
            related.Add((reader.GetInt64(0), reader.GetInt64(1)));
            related.Add((reader.GetInt64(1), reader.GetInt64(0)));
        }
        bool Apart(long x, long y) => x != y && !related.Contains((x, y));

        foreach (var id in reservations.Keys.Union(wanting.Keys))
        {
            var plans = wanting.GetValueOrDefault(id) ?? [];
            var conflict = plans.Any(x => plans.Any(y => Apart(x, y)))
                || (holders.TryGetValue(id, out var h) && plans.Any(p => h.Kind != "plan" || Apart(p, h.Id)));
            result[id] = new ResourceClaims(reservations.GetValueOrDefault(id), wanted.GetValueOrDefault(id) ?? [], conflict);
        }
        return result;
    }
}

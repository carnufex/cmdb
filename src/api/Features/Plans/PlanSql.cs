using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Trace;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>Reads and small writes shared by the plan endpoints (#24).</summary>
internal static class PlanSql
{
    public static string Actor(ClaimsPrincipal user) => user.Identity?.Name ?? user.FindFirstValue(CmdbClaims.Subject) ?? "?";

    public static Task ConflictAsync(HttpContext http, string message, CancellationToken ct) =>
        TypedResults.Problem(message, statusCode: StatusCodes.Status409Conflict).ExecuteAsync(http);

    /// <summary>Plans by id, or all when <paramref name="ids"/> is null.</summary>
    public static async Task<List<PlanSummary>> SummariesAsync(NpgsqlDataSource db, long[]? ids, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {PlanViews.Columns}, (SELECT count(*) FROM plan_operation o WHERE o.plan_id = p.id)::int
            FROM plan p WHERE $1::bigint[] IS NULL OR p.id = ANY($1)
            """);
        cmd.Parameters.Add(new() { Value = (object?)ids ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PlanSummary>();
        while (await reader.ReadAsync(ct))
        {
            var p = PlanViews.ReadPlan(reader);
            list.Add(new PlanSummary(p.Id, p.Name, p.Description, p.Status, p.Flag, p.CreatedBy, p.CreatedAt, p.UpdatedAt,
                p.AppliedBy, p.AppliedAt, p.DependsOn, reader.GetInt32(12)));
        }
        return list;
    }

    /// <summary>Why the dependencies cannot be used, or null: each must exist, be visible and not be cancelled.</summary>
    public static async Task<string?> CheckDependenciesAsync(NpgsqlDataSource db, long[] dependsOn, UserScope scope, CancellationToken ct)
    {
        if (dependsOn.Length == 0)
        {
            return null;
        }
        var found = (await SummariesAsync(db, dependsOn, ct)).Where(p => scope.SeesPlan(p.Id)).ToDictionary(p => p.Id);
        if (dependsOn.FirstOrDefault(id => !found.ContainsKey(id)) is var missing and not 0)
        {
            return $"Plan {missing} finns inte.";
        }
        return found.Values.FirstOrDefault(p => p.Status == "cancelled") is { } cancelled
            ? $"Planen {cancelled.Name} är avbruten."
            : null;
    }

    /// <summary>Whether <paramref name="planId"/> depending on these plans would close a cycle.</summary>
    public static async Task<bool> WouldCycleAsync(NpgsqlDataSource db, long planId, long[] dependsOn, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH RECURSIVE up(id) AS (
                SELECT unnest($2::bigint[])
                UNION
                SELECT d.depends_on_id FROM plan_dependency d JOIN up ON up.id = d.plan_id
            )
            SELECT EXISTS (SELECT 1 FROM up WHERE id = $1)
            """);
        cmd.Parameters.Add(new() { Value = planId });
        cmd.Parameters.Add(new() { Value = dependsOn });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task InsertDependenciesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long planId, long[] dependsOn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO plan_dependency (plan_id, depends_on_id) SELECT $1, unnest($2::bigint[])", conn, tx);
        cmd.Parameters.Add(new() { Value = planId });
        cmd.Parameters.Add(new() { Value = dependsOn });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>An edit: a new version for the view cache, and any flag is cleared since the plan is being dealt with.</summary>
    public static async Task TouchAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long planId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE plan SET version = version + 1, flag = NULL, updated_at = now() WHERE id = $1", conn, tx);
        cmd.Parameters.Add(new() { Value = planId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Draft plans building on the plan, directly or through others, in id order.</summary>
    public static async Task<List<long>> DependentsAsync(NpgsqlDataSource db, long planId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH RECURSIVE down(id) AS (
                SELECT d.plan_id FROM plan_dependency d WHERE d.depends_on_id = $1
                UNION
                SELECT d.plan_id FROM plan_dependency d JOIN down ON down.id = d.depends_on_id
            )
            SELECT p.id FROM plan p JOIN down ON down.id = p.id WHERE p.status = 'draft' ORDER BY p.id
            """);
        cmd.Parameters.Add(new() { Value = planId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>Marks a plan for attention; the version changes so its cached view is rebuilt.</summary>
    public static async Task FlagAsync(NpgsqlDataSource db, long planId, string reason, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("UPDATE plan SET flag = $2, version = version + 1, updated_at = now() WHERE id = $1");
        cmd.Parameters.Add(new() { Value = planId });
        cmd.Parameters.Add(new() { Value = reason });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<bool> ObjectVisibleAsync(NpgsqlDataSource db, string type, long id, UserScope scope, CancellationToken ct)
    {
        var sql = type switch
        {
            "site" => $"SELECT EXISTS (SELECT 1 FROM site s WHERE s.id = $1 AND {ScopeSql.Site("s.id", 2)})",
            "equipment" => $"SELECT EXISTS (SELECT 1 FROM equipment e WHERE e.id = $1 AND {ScopeSql.Site("e.site_id", 2)})",
            _ => $"SELECT EXISTS (SELECT 1 FROM cable c WHERE c.id = $1 AND {ScopeSql.Cable("c.id", 2)})",
        };
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(scope.Parameter());
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<List<PlanOp>> OperationsAsync(NpgsqlDataSource db, long planId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, plan_id, seq, kind, payload::text, created_by, created_at FROM plan_operation WHERE plan_id = $1 ORDER BY seq
            """);
        cmd.Parameters.Add(new() { Value = planId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PlanOp>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(PlanViews.ReadOperation(reader));
        }
        return list;
    }

    /// <summary>
    /// Operations in words. Terminals and objects outside the caller's scopes are placeholders (#22), as in a trace.
    /// </summary>
    public static async Task<List<PlanOperationView>> DescribeAsync(NpgsqlDataSource db, Cmdb.Graph.Graph graph, GraphMask mask,
        UserScope scope, IReadOnlyList<PlanOp> operations, IReadOnlyDictionary<long, GraphChangeProblem> problems, CancellationToken ct)
    {
        var terminals = operations.Where(o => o.Edge is not null).SelectMany(o => new[] { o.A, o.B }).Distinct().ToArray();
        var names = await TraceNames.LoadAsync(db, terminals, [], [], ct);
        var objects = await ObjectsAsync(db, operations.Where(o => o.Edge is null).Select(o => (o.ObjectType!, o.ObjectId)).Distinct().ToList(), scope, ct);

        TraceHop Hop(long terminal) =>
            graph.TryGetNode(terminal, out var node) && !Visible(graph, mask, node) ? TraceNames.Placeholder(null) : names.Hop(terminal, null);

        var list = new List<PlanOperationView>();
        foreach (var op in operations)
        {
            var problem = problems.TryGetValue(op.Id, out var p) ? PlanKinds.Problem(p) : null;
            if (op.Edge is not null)
            {
                var (a, b) = (Hop(op.A), Hop(op.B));
                var summary = op.Kind == "connect"
                    ? $"Koppla {a.Label} till {b.Label} ({ConnectionName(op.ConnectionKind)})"
                    : $"Koppla bort {a.Label} från {b.Label}";
                list.Add(new PlanOperationView(op.Id, op.PlanId, op.Seq, op.Kind, summary, [a, b], null, op.ConnectionKind, null, null,
                    problem, op.CreatedBy, op.CreatedAt));
                continue;
            }
            var found = objects.TryGetValue((op.ObjectType!, op.ObjectId), out var o);
            var target = !found ? new ObjectRef(op.ObjectType!, op.ObjectId, $"#{op.ObjectId}")
                : o.Visible ? o.Ref : ObjectRef.Hidden(op.ObjectType!);
            var lifecycle = op.Payload.TryGetProperty("lifecycle", out var l) ? l.GetString() : null;
            var name = op.Payload.TryGetProperty("name", out var n) ? n.GetString() : null;
            var text = op.Kind == "set_lifecycle"
                ? $"Sätt livscykel för {target.Code} till {LifecycleName(lifecycle)}"
                : $"Byt namn på {target.Code} till {name}";
            if (problem is null && !found)
            {
                problem = "Objektet finns inte längre.";
            }
            list.Add(new PlanOperationView(op.Id, op.PlanId, op.Seq, op.Kind, text, [], target, null, lifecycle, name, problem,
                op.CreatedBy, op.CreatedAt));
        }
        return list;
    }

    public static async Task<List<PlanSite>> SitePositionsAsync(NpgsqlDataSource db, long[] ids, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site WHERE id = ANY($1)");
        cmd.Parameters.Add(new() { Value = ids });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PlanSite>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PlanSite(reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2)));
        }
        return list;
    }

    private static async Task<Dictionary<(string, long), (ObjectRef Ref, bool Visible)>> ObjectsAsync(NpgsqlDataSource db,
        List<(string Type, long Id)> refs, UserScope scope, CancellationToken ct)
    {
        var result = new Dictionary<(string, long), (ObjectRef Ref, bool Visible)>();
        if (refs.Count == 0)
        {
            return result;
        }
        await using var cmd = db.CreateCommand($"""
            SELECT 'site', s.id, s.code, s.name, s.lifecycle::text, {ScopeSql.Site("s.id", 4)} FROM site s WHERE s.id = ANY($1)
            UNION ALL SELECT 'equipment', e.id, e.name, NULL, e.lifecycle::text, {ScopeSql.Site("e.site_id", 4)} FROM equipment e WHERE e.id = ANY($2)
            UNION ALL SELECT 'cable', c.id, c.code, NULL, c.lifecycle::text, {ScopeSql.Cable("c.id", 4)} FROM cable c WHERE c.id = ANY($3)
            """);
        cmd.Parameters.Add(new() { Value = refs.Where(r => r.Type == "site").Select(r => r.Id).ToArray() });
        cmd.Parameters.Add(new() { Value = refs.Where(r => r.Type == "equipment").Select(r => r.Id).ToArray() });
        cmd.Parameters.Add(new() { Value = refs.Where(r => r.Type == "cable").Select(r => r.Id).ToArray() });
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetString(0);
            result[(type, reader.GetInt64(1))] = (new ObjectRef(type, reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)), reader.GetBoolean(5));
        }
        return result;
    }

    private static bool Visible(Cmdb.Graph.Graph g, GraphMask mask, int node)
    {
        var site = g.SiteIndexOfNode(node);
        return site >= 0 ? mask.Sites[site] : mask.Cables[g.CableIndexOfNode(node)];
    }

    private static string ConnectionName(string? kind) => kind switch
    {
        "splice" => "skarv",
        "termination" => "terminering",
        "internal" => "intern",
        _ => "patch",
    };

    private static string LifecycleName(string? lifecycle) => lifecycle switch
    {
        "planned" => "planerad",
        "under_construction" => "under byggnation",
        "in_service" => "i drift",
        "decommissioning" => "under avveckling",
        _ => "borttagen",
    };
}

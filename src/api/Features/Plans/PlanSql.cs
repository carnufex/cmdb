using System.Text.Json;
using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Reservations;
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
            SELECT {PlanViews.Columns}, (SELECT count(*) FROM plan_operation o WHERE o.plan_id = p.id)::int, p.applied_exception
            FROM plan p WHERE $1::bigint[] IS NULL OR p.id = ANY($1)
            """);
        cmd.Parameters.Add(new() { Value = (object?)ids ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint });
        var list = new List<PlanSummary>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var p = PlanViews.ReadPlan(reader);
                list.Add(new PlanSummary(p.Id, p.Name, p.Description, p.Status, p.Flag, p.CreatedBy, p.CreatedAt, p.UpdatedAt,
                    p.AppliedBy, p.AppliedAt, p.DependsOn, reader.GetInt32(14), CreatedVia: p.CreatedVia, Client: p.Client,
                    Exception: reader.IsDBNull(15) ? null : reader.GetString(15)));
            }
        }
        // Operations in conflict with others' claims (#25), per plan.
        var conflicts = (await ClaimsSql.ConflictsAsync(db, ids, ct)).GroupBy(c => c.PlanId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.OperationId).Distinct().Count());
        return [.. list.Select(p => p with { Conflicts = conflicts.GetValueOrDefault(p.Id) })];
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

    public static async Task ReleaseReservationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long planId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE reservation SET released_at = now() WHERE holder_kind = 'plan' AND holder_id = $1 AND released_at IS NULL", conn, tx);
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
            "service" => $"SELECT EXISTS (SELECT 1 FROM service s WHERE s.id = $1 AND {ScopeSql.Service("s.id", 2)})",
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
        UserScope scope, IReadOnlyList<PlanOp> operations, IReadOnlyDictionary<long, GraphChangeProblem> problems, CancellationToken ct,
        IReadOnlyList<PlanOp>? context = null)
    {
        // Objects the plan (and the plans under it) create are named from their operations (#107).
        var planned = await PlannedNames.BuildAsync(db, context ?? operations, ct);
        var terminals = operations.Where(o => o.Edge is not null).SelectMany(o => new[] { o.A, o.B }).Distinct().ToArray();
        var conflicts = (await ClaimsSql.ConflictsAsync(db, [.. operations.Select(o => o.PlanId).Distinct()], ct)).ToLookup(c => c.OperationId);
        var names = await TraceNames.LoadAsync(db, [.. terminals.Where(t => t > 0)], [], [], ct);
        names.AddPlanned(planned.Terminals);
        var objects = await ObjectsAsync(db, [.. operations.Where(o => o.Kind is "set_lifecycle" or "rename" or "set_attributes" or "remove" or "set_classification" or "move" or "set_conductor_usage").Select(o => (o.ObjectType!, o.ObjectId)).Distinct()],
            scope, ct);

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
                    problem, op.CreatedBy, op.CreatedAt, [.. conflicts[op.Id].Select(c => c.Message(scope))],
                    conflicts[op.Id].Any(c => c.Blocking)));
                continue;
            }
            if (op.Kind == "split_cable")
            {
                var partA = planned.Objects[("cable", Planned.ObjectId(op.Id))];
                var site = planned.Terminals[CableSplit.InnerA(op.Id, 1)].Site;
                var total = CableSplit.Conductors(op.Payload).Count;
                var terminated = CableSplit.Terminated(op.Payload).Count;
                var splitText = $"Sätt in {site?.Code} {site?.Name} i kabel {op.Payload.GetProperty("code").GetString()}: " +
                    $"{total - terminated} ledare skarvas igenom" + (terminated > 0 ? $", {terminated} termineras i siten" : "");
                list.Add(new PlanOperationView(op.Id, op.PlanId, op.Seq, op.Kind, splitText, [], partA, null, null, null, problem,
                    op.CreatedBy, op.CreatedAt, [], false));
                continue;
            }
            if (op.Kind.StartsWith("create_", StringComparison.Ordinal))
            {
                var (created, what) = planned.Describe(op);
                list.Add(new PlanOperationView(op.Id, op.PlanId, op.Seq, op.Kind, created, [], what, null, null, what.Name, problem,
                    op.CreatedBy, op.CreatedAt, [], false));
                continue;
            }
            var found = objects.TryGetValue((op.ObjectType!, op.ObjectId), out var o) || (op.ObjectId < 0 && planned.Objects.ContainsKey((op.ObjectType!, op.ObjectId)));
            if (op.ObjectId < 0 && planned.Objects.TryGetValue((op.ObjectType!, op.ObjectId), out var plannedObject))
            {
                (o.Ref, o.Visible) = (plannedObject, true);
            }
            var target = !found ? new ObjectRef(op.ObjectType!, op.ObjectId, $"#{op.ObjectId}")
                : o.Visible ? o.Ref : ObjectRef.Hidden(op.ObjectType!);
            var lifecycle = op.Payload.TryGetProperty("lifecycle", out var l) ? l.GetString() : null;
            var name = op.Payload.TryGetProperty("name", out var n) ? n.GetString() : null;
            var text = op.Kind switch
            {
                "set_lifecycle" => $"Sätt livscykel för {target.Code} till {LifecycleName(lifecycle)}",
                "set_attributes" => $"Ändra attribut på {target.Code}: {AttributeText(op.Payload.GetProperty("attributes"))}",
                "set_classification" => ClassificationText(op, target),
                "remove" when op.ObjectType == "site" => $"Ta bort site {target.Code} {target.Name}".TrimEnd() +
                    $" med {ObjectRemoval.Objects(op.Payload).Equipment.Length} utrustningar och {ObjectRemoval.Objects(op.Payload).Cables.Length} kablar",
                "remove" => $"Ta bort {(op.ObjectType == "cable" ? "kabel" : "utrustning")} {target.Code}",
                "move" => MoveText(op.Payload),
                "set_conductor_usage" => UsageText(op.Payload, target.Code),
                _ => $"Byt namn på {target.Code} till {name}",
            };
            if (problem is null && !found)
            {
                problem = "Objektet finns inte längre.";
            }
            list.Add(new PlanOperationView(op.Id, op.PlanId, op.Seq, op.Kind, text, [], target, null, lifecycle, name, problem,
                op.CreatedBy, op.CreatedAt, [], false));
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

    /// <summary>A stated conductor usage (#238), in words: "Sätt ledare 1–4, 7 i kabel K-000123 till svartfiber".</summary>
    private static string UsageText(JsonElement p, string cable)
    {
        var numbers = p.GetProperty("conductors").EnumerateArray().Select(n => n.GetInt32()).Order().ToList();
        var runs = new List<string>();
        for (var i = 0; i < numbers.Count;)
        {
            var j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1)
            {
                j++;
            }
            runs.Add(i == j ? $"{numbers[i]}" : $"{numbers[i]}–{numbers[j]}");
            i = j + 1;
        }
        var usage = p.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var word = usage switch { "dark" => "släckt", "dark_fibre" => "svartfiber", "spare" => "reserv", _ => null };
        return word is null
            ? $"Ta bort angiven användning för ledare {string.Join(", ", runs)} i kabel {cable}"
            : $"Sätt ledare {string.Join(", ", runs)} i kabel {cable} till {word}";
    }

    /// <summary>What a move does (#187), in words: the object, where from and where to.</summary>
    private static string MoveText(JsonElement p)
    {
        string Text(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var (code, from, to) = (Text("code"), Text("fromCode"), Text("siteCode"));
        if (p.GetProperty("type").GetString() == "cable")
        {
            return $"Flytta ände {Text("end")} av kabel {code} från {from} till {to}";
        }
        var where = Text("rack") is { Length: > 0 } rack ? $", rack {rack}" : "";
        var unit = p.TryGetProperty("position", out var at) && at.ValueKind == JsonValueKind.Number ? $", enhet {at.GetInt32()}" : "";
        return from == to
            ? $"Flytta utrustning {code} inom {to}{where}{unit}"
            : $"Flytta utrustning {code} från {from} till {to}{where}{unit}";
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
            UNION ALL SELECT 'service', v.id, v.code, v.name, 'in_service', {ScopeSql.Service("v.id", 4)} FROM service v WHERE v.id = ANY($5)
            """);
        cmd.Parameters.Add(new() { Value = refs.Where(r => r.Type == "site").Select(r => r.Id).ToArray() });
        cmd.Parameters.Add(new() { Value = refs.Where(r => r.Type == "equipment").Select(r => r.Id).ToArray() });
        cmd.Parameters.Add(new() { Value = refs.Where(r => r.Type == "cable").Select(r => r.Id).ToArray() });
        cmd.Parameters.Add(scope.Parameter());
        cmd.Parameters.Add(new() { Value = refs.Where(r => r.Type == "service").Select(r => r.Id).ToArray() });
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
        return mask.NodeVisible(g, node);
    }

    private static string ClassificationText(PlanOp op, ObjectRef target)
    {
        var schema = Cmdb.Catalog.ClassificationCatalog.Current.Find(op.Payload.GetProperty("schema").GetString()!);
        var name = schema?.Name ?? op.Payload.GetProperty("schema").GetString();
        if (op.Payload.TryGetProperty("level", out var level) && level.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            return $"Sätt {name} för {target.Code} till {level.GetInt32()} ({schema?.Level(level.GetInt32())?.Name})";
        }
        return $"Ta bort {name} från {target.Code}";
    }

    private static string AttributeText(System.Text.Json.JsonElement attributes) => string.Join(", ", attributes.EnumerateObject()
        .Select(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Null ? $"{p.Name} tas bort" : $"{p.Name} = {p.Value}"));

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

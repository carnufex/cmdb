using System.Collections.Concurrent;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>A plan as stored, with the ids of the plans it builds on.</summary>
public sealed record PlanRow(long Id, string Name, string Description, string Status, string? Flag, int Version, string CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? AppliedBy, DateTimeOffset? AppliedAt, long[] DependsOn);

/// <summary>One operation of a plan. <see cref="Payload"/> is the stored JSON arguments.</summary>
public sealed record PlanOp(long Id, long PlanId, int Seq, string Kind, JsonElement Payload, string CreatedBy, DateTimeOffset CreatedAt)
{
    public long A => Payload.GetProperty("a").GetInt64();
    public long B => Payload.GetProperty("b").GetInt64();
    public string? ConnectionKind => Payload.TryGetProperty("kind", out var k) ? k.GetString() : null;
    public string? ObjectType => Payload.TryGetProperty("type", out var t) ? t.GetString() : null;
    public long ObjectId => Payload.GetProperty("id").GetInt64();

    /// <summary>What the operation does to the graph, if anything: connects and disconnects.</summary>
    public GraphEdgeChange? Edge => Kind switch
    {
        "connect" => new GraphEdgeChange(A, B, PlanKinds.Edge(ConnectionKind!), Add: true),
        "disconnect" => new GraphEdgeChange(A, B, EdgeKind.Patch, Add: false),
        _ => null,
    };
}

/// <summary>
/// A plan with everything its view is made of: the draft plans it builds on, in dependency order, then the plan itself,
/// and their operations in that order. Applied plans are production already; cancelled ones contribute nothing.
/// </summary>
public sealed record PlanChain(PlanRow Plan, IReadOnlyList<PlanRow> Plans, IReadOnlyList<PlanOp> Operations)
{
    /// <summary>Identifies the view's content, for caching.</summary>
    public string Signature => string.Join(',', Plans.Select(p => $"{p.Id}:{p.Version}"));
}

/// <summary>A plan's view of the network: production + dependencies + the plan (ADR-0005), and the operations that do not fit.</summary>
public sealed record PlanView(PlanChain Chain, Cmdb.Graph.Graph Graph, IReadOnlyDictionary<long, GraphChangeProblem> Problems);

internal static class PlanKinds
{
    public static readonly string[] Operations = ["connect", "disconnect", "set_lifecycle", "rename"];
    public static readonly string[] Connections = ["patch", "splice", "termination", "internal"];

    public static EdgeKind Edge(string kind) => kind switch
    {
        "splice" => EdgeKind.Splice,
        "termination" => EdgeKind.Termination,
        "internal" => EdgeKind.Internal,
        _ => EdgeKind.Patch,
    };

    public static string Problem(GraphChangeProblem problem) => problem switch
    {
        GraphChangeProblem.UnknownTerminal => "Terminalen finns inte längre.",
        GraphChangeProblem.AlreadyConnected => "Terminalerna är redan kopplade.",
        GraphChangeProblem.NotConnected => "Terminalerna är inte kopplade.",
        _ => "En terminal kan inte kopplas till sig själv.",
    };
}

/// <summary>
/// Plan views as base graph + delta (#24), cached per production graph and plan content, so switching between
/// production and a plan does not rebuild anything. Loading a chain is one small query.
/// </summary>
public sealed class PlanViews(SystemDb system)
{
    private const int MaxCached = 32;
    private readonly ConcurrentDictionary<(Cmdb.Graph.Graph Base, long Plan), (string Signature, PlanView View)> _cache = new();

    /// <summary>The plan's view, or null when the plan does not exist or the caller's scopes do not show it.</summary>
    public async Task<PlanView?> GetAsync(Cmdb.Graph.Graph production, long planId, UserScope scope, CancellationToken ct)
    {
        if (!scope.SeesPlan(planId) || await LoadChainAsync(system.Source, planId, ct) is not { } chain)
        {
            return null;
        }
        return View(production, chain);
    }

    /// <summary>The view of a chain on a given production graph; also used to re-check dependents when a plan is applied.</summary>
    public PlanView View(Cmdb.Graph.Graph production, PlanChain chain)
    {
        // Keyed on the graph instance: production, or production as it will be after an apply (a view itself).
        var key = (production, chain.Plan.Id);
        if (_cache.TryGetValue(key, out var hit) && hit.Signature == chain.Signature)
        {
            return hit.View;
        }
        var edgeOps = chain.Operations.Where(o => o.Edge is not null).ToList();
        var (graph, issues) = production.WithChanges([.. edgeOps.Select(o => o.Edge!)]);
        var view = new PlanView(chain, graph, issues.ToDictionary(i => edgeOps[i.Index].Id, i => i.Problem));
        if (_cache.Count >= MaxCached)
        {
            // Old production graphs and plans nobody looks at; the next request rebuilds in milliseconds.
            _cache.Clear();
        }
        _cache[key] = (chain.Signature, view);
        return view;
    }

    public static async Task<PlanChain?> LoadChainAsync(NpgsqlDataSource db, long planId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new($"""
                    WITH RECURSIVE chain(id) AS (
                        SELECT $1::bigint
                        UNION
                        SELECT d.depends_on_id FROM plan_dependency d JOIN chain c ON c.id = d.plan_id
                    )
                    SELECT {Columns} FROM plan p JOIN chain c ON c.id = p.id
                    """) { Parameters = { new() { Value = planId } } },
                new("""
                    WITH RECURSIVE chain(id) AS (
                        SELECT $1::bigint
                        UNION
                        SELECT d.depends_on_id FROM plan_dependency d JOIN chain c ON c.id = d.plan_id
                    )
                    SELECT o.id, o.plan_id, o.seq, o.kind, o.payload::text, o.created_by, o.created_at
                    FROM plan_operation o JOIN chain c ON c.id = o.plan_id JOIN plan p ON p.id = o.plan_id
                    WHERE p.status = 'draft'
                    ORDER BY o.plan_id, o.seq
                    """) { Parameters = { new() { Value = planId } } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);
        var plans = new Dictionary<long, PlanRow>();
        while (await reader.ReadAsync(ct))
        {
            var row = ReadPlan(reader);
            plans[row.Id] = row;
        }
        if (!plans.TryGetValue(planId, out var plan))
        {
            return null;
        }
        await reader.NextResultAsync(ct);
        var operations = new Dictionary<long, List<PlanOp>>();
        while (await reader.ReadAsync(ct))
        {
            var op = ReadOperation(reader);
            if (!operations.TryGetValue(op.PlanId, out var list))
            {
                operations[op.PlanId] = list = [];
            }
            list.Add(op);
        }

        // Dependencies first (Kahn's algorithm, lowest id first among equals so the order is deterministic).
        var drafts = plans.Values.Where(p => p.Status == "draft" || p.Id == planId).ToDictionary(p => p.Id);
        var waiting = drafts.Values.ToDictionary(p => p.Id, p => p.DependsOn.Count(drafts.ContainsKey));
        var ready = new SortedSet<long>(waiting.Where(w => w.Value == 0).Select(w => w.Key));
        var ordered = new List<PlanRow>();
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            ordered.Add(drafts[next]);
            foreach (var dependent in drafts.Values.Where(p => p.DependsOn.Contains(next)))
            {
                if (--waiting[dependent.Id] == 0)
                {
                    ready.Add(dependent.Id);
                }
            }
        }
        return new PlanChain(plan, ordered, [.. ordered.SelectMany(p => operations.GetValueOrDefault(p.Id) ?? [])]);
    }

    internal const string Columns = """
        p.id, p.name, p.description, p.status, p.flag, p.version, p.created_by, p.created_at, p.updated_at, p.applied_by, p.applied_at,
        ARRAY(SELECT d.depends_on_id FROM plan_dependency d WHERE d.plan_id = p.id ORDER BY d.depends_on_id)
        """;

    internal static PlanRow ReadPlan(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5),
        r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<long[]>(11));

    internal static PlanOp ReadOperation(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), JsonDocument.Parse(r.GetString(4)).RootElement.Clone(),
        r.GetString(5), r.GetFieldValue<DateTimeOffset>(6));
}

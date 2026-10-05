using System.Collections.Concurrent;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>A plan as stored, with the ids of the plans it builds on.</summary>
public sealed record PlanRow(long Id, string Name, string Description, string Status, string? Flag, int Version, string CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? AppliedBy, DateTimeOffset? AppliedAt, long[] DependsOn,
    string CreatedVia = "api", string? Client = null);

/// <summary>One operation of a plan. <see cref="Payload"/> is the stored JSON arguments.</summary>
public sealed record PlanOp(long Id, long PlanId, int Seq, string Kind, JsonElement Payload, string CreatedBy, DateTimeOffset CreatedAt)
{
    public long A => Payload.GetProperty("a").GetInt64();
    public long B => Payload.GetProperty("b").GetInt64();
    public string? ConnectionKind => Payload.TryGetProperty("kind", out var k) ? k.GetString() : null;
    public string? ObjectType => Payload.TryGetProperty("type", out var t) ? t.GetString() : null;
    public long ObjectId => Payload.GetProperty("id").GetInt64();

    /// <summary>A connect or disconnect: the connection it adds or removes.</summary>
    public GraphEdgeChange? Edge => Kind switch
    {
        "connect" => new GraphEdgeChange(A, B, PlanKinds.Edge(ConnectionKind!), Add: true),
        "disconnect" => new GraphEdgeChange(A, B, EdgeKind.Patch, Add: false),
        _ => null,
    };

    /// <summary>
    /// What the operation does to the graph, if anything: a connection, or a planned site, equipment or cable (#107)
    /// with the ids <see cref="Planned"/> gives it.
    /// </summary>
    public GraphChange? Change => Kind switch
    {
        "connect" or "disconnect" => Edge,
        "create_site" => new GraphNewSite(Planned.ObjectId(Id)),
        "create_equipment" => new GraphNewEquipment(Planned.ObjectId(Id), Payload.GetProperty("site").GetInt64(),
            [.. Enumerable.Range(1, Planned.PortCount(Payload.GetProperty("typeKey").GetString()!)).Select(n => Planned.Terminal(Id, n))]),
        "create_cable" => new GraphNewCable(Planned.ObjectId(Id),
            [.. Enumerable.Range(1, Planned.ConductorCount(Payload.GetProperty("typeKey").GetString()!))
                .Select(k => new GraphNewConductor(Planned.Conductor(Id, k), Planned.Terminal(Id, (2 * k) - 1), Planned.Terminal(Id, 2 * k)))]),
        _ => null,
    };

    /// <summary>Planned ids this operation refers to that another operation creates: sites, terminals.</summary>
    public IEnumerable<long> PlannedReferences => Kind switch
    {
        "connect" or "disconnect" => new[] { A, B }.Where(id => id < 0),
        "create_equipment" => new[] { Payload.GetProperty("site").GetInt64() }.Where(id => id < 0),
        "create_cable" => new[] { Payload.GetProperty("a").GetInt64(), Payload.GetProperty("b").GetInt64() }.Where(id => id < 0),
        "split_cable" => new[] { Payload.GetProperty("site").GetInt64() }.Where(id => id < 0),
        _ => [],
    };
}

/// <summary>
/// Ids of objects a plan creates (#107): negative, so they never meet production's, and derived from the operation, so
/// later operations and plans building on this one can refer to them. A planned object is <c>-op</c>; its n-th terminal
/// (port n, or conductor k's ends 2k-1 and 2k) is <c>-(op × 10000 + n)</c>, and conductor k is <c>-(op × 10000 + 5000 + k)</c>,
/// so terminals and conductors never share an id.
/// </summary>
public static class Planned
{
    public const int PerObject = 10_000;

    /// <summary>Conductors start here within an operation's range; terminals stay below.</summary>
    private const int Conductors = 5_000;

    public static long ObjectId(long op) => -op;

    public static long Terminal(long op, int n) => -((op * PerObject) + n);

    public static long Conductor(long op, int k) => -((op * PerObject) + Conductors + k);

    public static int PortCount(string typeKey) =>
        Cmdb.Catalog.TypeCatalog.Embedded.Find(typeKey) is { } type ? Cmdb.Catalog.PortExpansion.Expand(type).Count : 0;

    public static int ConductorCount(string typeKey) =>
        Cmdb.Catalog.TypeCatalog.Embedded.CableTypes.FirstOrDefault(t => t.Key == typeKey)?.ConductorCount ?? 0;
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
    public static readonly string[] Operations =
        ["connect", "disconnect", "set_lifecycle", "rename", "set_attributes", "create_site", "create_equipment", "create_cable", "split_cable", "remove", "set_classification"];

    public static readonly string[] SiteTypes = ["hub", "aggregation", "radio", "cabinet", "splice"];
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
        GraphChangeProblem.Occupied => "Porten eller fibern är redan upptagen av en koppling av samma slag.",
        GraphChangeProblem.InvalidObject => "Objektet finns redan.",
        GraphChangeProblem.UnknownCircuit => "Kretsen finns inte.",
        GraphChangeProblem.CarriesCircuits => "Kretsar går genom objektet. Flytta dem först, annars bryts tjänsterna.",
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
        var (graph, problems) = Build(production, chain.Operations);
        var view = new PlanView(chain, graph, problems);
        if (_cache.Count >= MaxCached)
        {
            // Old production graphs and plans nobody looks at; the next request rebuilds in milliseconds.
            _cache.Clear();
        }
        _cache[key] = (chain.Signature, view);
        return view;
    }

    /// <summary>
    /// Production with the operations applied in order, and the operations that do not fit. Most operations are fixed
    /// graph changes; a cable split (#168) is worked out against the view just before it, so the changes before it are
    /// applied first. <paramref name="map"/> rewrites planned ids, e.g. to the ones an apply gave them.
    /// </summary>
    public static (Cmdb.Graph.Graph Graph, Dictionary<long, GraphChangeProblem> Problems) Build(Cmdb.Graph.Graph production,
        IReadOnlyList<PlanOp> operations, Func<GraphChange, GraphChange>? map = null)
    {
        var graph = production;
        var problems = new Dictionary<long, GraphChangeProblem>();
        var pending = new List<(long Op, GraphChange Change)>();
        var applied = false;

        void Flush()
        {
            if (pending.Count == 0 && applied)
            {
                return;
            }
            var (view, issues) = graph.WithChanges([.. pending.Select(p => map is null ? p.Change : map(p.Change))]);
            foreach (var issue in issues)
            {
                problems.TryAdd(pending[issue.Index].Op, issue.Problem);
            }
            (graph, applied) = (view, true);
            pending.Clear();
        }

        foreach (var op in operations)
        {
            if (op.Kind == "split_cable")
            {
                Flush();
                pending.AddRange(CableSplit.Changes(graph, op).Select(c => (op.Id, c)));
                Flush();
            }
            else if (op.Kind == "remove")
            {
                // Worked out against the view so far (#172): a circuit added through the object since is a problem.
                Flush();
                var (equipment, cables) = ObjectRemoval.Objects(op.Payload);
                if (ObjectRemoval.Circuits(graph, ObjectRemoval.Nodes(graph, equipment, cables)).Count > 0)
                {
                    problems.TryAdd(op.Id, GraphChangeProblem.CarriesCircuits);
                }
                pending.AddRange(ObjectRemoval.Changes(graph, op).Select(c => (op.Id, c)));
                Flush();
            }
            else if (op.Change is { } change)
            {
                pending.Add((op.Id, change));
            }
        }
        Flush();
        return (graph, problems);
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
        ARRAY(SELECT d.depends_on_id FROM plan_dependency d WHERE d.plan_id = p.id ORDER BY d.depends_on_id),
        p.created_via, p.client
        """;

    internal static PlanRow ReadPlan(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5),
        r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<long[]>(11), r.GetString(12),
        r.IsDBNull(13) ? null : r.GetString(13));

    internal static PlanOp ReadOperation(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), JsonDocument.Parse(r.GetString(4)).RootElement.Clone(),
        r.GetString(5), r.GetFieldValue<DateTimeOffset>(6));
}

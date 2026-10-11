using System.Diagnostics;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Trace;
using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Plans;

/// <summary>A circuit the plan touches, and whether its path holds before and after.</summary>
/// <param name="Before">intact, broken or absent (the plan creates it) in production.</param>
/// <param name="After">intact, broken or absent (the plan removes it) in the plan's view.</param>
/// <param name="Services">Services the circuit carries, directly or through circuits riding on it.</param>
public sealed record PlanImpactCircuit(ObjectRef Circuit, string Layer, string Before, string After, int Services);

/// <param name="Before">intact, degraded (some of its circuits broken) or broken (all of them).</param>
public sealed record PlanImpactService(ObjectRef Service, string Before, string After);

/// <summary>What the plan touches (#252): circuits through what it changes, the circuits riding on those, and services.</summary>
/// <param name="Broken">Circuits whose path holds in production but not in the plan.</param>
/// <param name="Truncated">More circuits than <see cref="PlanImpactEndpoint.Listed"/>: the changed ones come first.</param>
public sealed record PlanImpact(IReadOnlyList<PlanImpactCircuit> Circuits, IReadOnlyList<PlanImpactService> Services, int Broken,
    int HiddenCircuits, int HiddenServices, bool Truncated, double ElapsedMs);

/// <summary>
/// The plan's impact (#252): the circuits passing the terminals it connects and disconnects and the objects it removes,
/// moves, splits or retires, the circuits riding on them, and those whose path or links it changes; each with whether its
/// path holds in production and in the plan, and the services they carry. A physical circuit holds when a walk from its
/// first hop reaches its last; a circuit riding on others holds when all its carriers do. Budget: 200 ms.
/// </summary>
public sealed class PlanImpactEndpoint(RequestDb db, GraphHolder holder, PlanViews views, ScopeMasks masks) : Endpoint<PlanIdRequest, PlanImpact>
{
    public override void Configure() => Get("/plans/{id}/impact");

    public const int Listed = 500;

    public override async Task HandleAsync(PlanIdRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var scope = HttpContext.Scope();
        var production = holder.Require();
        if (await views.GetAsync(production, req.Id, scope, ct) is not { } view)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var plan = view.Graph;
        var mask = await masks.GetAsync(production, scope, ct);
        var planMask = await masks.GetAsync(plan, scope, ct);
        var ops = view.Chain.Operations.Where(o => o.PlanId == req.Id).ToList();

        // The circuits each graph sees through what the plan touches, by id: production's and the plan's own (planned).
        var ids = new HashSet<long>();
        Touch(production, ops, ids);
        Touch(plan, ops, ids);

        var before = new Holds(production);
        var after = new Holds(plan);
        var rows = new List<(long Id, int? P, int? Q)>();
        var hidden = 0;
        foreach (var id in ids)
        {
            int? p = production.TryGetCircuit(id, out var pc) ? pc : null;
            int? q = plan.TryGetCircuit(id, out var qc) ? qc : null;
            if ((p is { } a && !mask.CircuitVisible(a)) || (q is { } b && !planMask.CircuitVisible(b)))
            {
                hidden++;
                continue;
            }
            rows.Add((id, p, q));
        }
        string State(Holds holds, int? c) => c is { } i ? holds.Of(i) ? "intact" : "broken" : "absent";
        var states = rows.Select(r => (r.Id, r.P, r.Q, Before: State(before, r.P), After: State(after, r.Q)))
            .OrderBy(r => r.Before == r.After ? 1 : 0).ThenBy(r => r.Id).ToList();
        var truncated = states.Count > Listed;
        var listed = states.Take(Listed).ToList();

        // Services through the listed circuits, with the state of all their circuits on each side.
        var services = new HashSet<long>();
        var hiddenServices = new HashSet<long>();
        var counts = new Dictionary<long, int>();
        foreach (var r in listed)
        {
            var carried = new HashSet<long>();
            if (r.P is { } p)
            {
                Carried(production, p, carried, hiddenServices, mask);
            }
            if (r.Q is { } q)
            {
                Carried(plan, q, carried, hiddenServices, planMask);
            }
            counts[r.Id] = carried.Count;
            services.UnionWith(carried);
        }
        var serviceRows = new List<(long Id, string Before, string After)>();
        foreach (var s in services)
        {
            serviceRows.Add((s, ServiceState(production, before, s), ServiceState(plan, after, s)));
        }
        var names = await TraceNames.LoadAsync(db, [], [.. listed.Select(r => r.Id).Where(id => id > 0)], [.. services.Where(id => id > 0)], ct);
        var planned = PlannedCircuits(view.Chain.Operations);

        await Send.OkAsync(new PlanImpact(
            [.. listed.Select(r => new PlanImpactCircuit(
                r.Id > 0 ? names.Circuit(r.Id) : new ObjectRef("circuit", r.Id, planned.GetValueOrDefault(r.Id, "Planerad krets")),
                TraceEndpoint.Layer((r.Q is { } q ? plan : production).LayerOf(r.Q ?? r.P!.Value)),
                r.Before, r.After, counts[r.Id]))],
            [.. serviceRows.OrderBy(s => s.Before == s.After ? 1 : 0).ThenBy(s => names.Service(s.Id).Code, StringComparer.Ordinal)
                .Select(s => new PlanImpactService(names.Service(s.Id), s.Before, s.After))],
            states.Count(s => s.Before == "intact" && s.After != "intact"),
            hidden,
            hiddenServices.Count,
            truncated,
            Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ct);
    }

    /// <summary>The circuits through what the operations touch in this graph, and those riding on them, by id.</summary>
    internal static void Touch(Cmdb.Graph.Graph g, IReadOnlyList<PlanOp> ops, HashSet<long> ids)
    {
        var nodes = new List<int>();
        void Node(long terminal)
        {
            if (g.TryGetNode(terminal, out var n))
            {
                nodes.Add(n);
            }
        }
        void Object(string? type, long id)
        {
            switch (type)
            {
                case "site" when g.TryGetSite(id, out var site):
                    foreach (var e in g.EquipmentAt(site))
                    {
                        nodes.AddRange(g.PortsOf(e).ToArray());
                    }
                    break;
                case "equipment" when g.TryGetEquipment(id, out var equipment):
                    nodes.AddRange(g.PortsOf(equipment).ToArray());
                    break;
                case "cable" when g.TryGetCable(id, out var cable):
                    nodes.AddRange(g.EndsOf(cable).ToArray());
                    break;
            }
        }
        foreach (var op in ops)
        {
            var p = op.Payload;
            switch (op.Kind)
            {
                case "connect" or "disconnect":
                    Node(op.A);
                    Node(op.B);
                    break;
                case "remove" or "move" or "set_lifecycle":
                    Object(op.ObjectType, op.ObjectId);
                    break;
                case "split_cable":
                    Object("cable", p.GetProperty("cable").GetInt64());
                    break;
                case "create_circuit":
                    ids.Add(Planned.ObjectId(op.Id));
                    break;
                case "set_circuit_path":
                    ids.Add(p.GetProperty("id").GetInt64());
                    break;
                case "link_circuit" or "link_service":
                    ids.Add(p.GetProperty("circuit").GetInt64());
                    break;
            }
        }
        if (nodes.Count > 0)
        {
            var result = GraphImpact.OfNodes(g, [.. nodes.Distinct()]);
            foreach (var c in result.Circuits)
            {
                ids.Add(g.CircuitId(c));
            }
        }
    }

    private static void Carried(Cmdb.Graph.Graph g, int circuit, HashSet<long> services, HashSet<long> hidden, GraphMask mask)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>([circuit]);
        while (stack.TryPop(out var c))
        {
            if (!seen.Add(c))
            {
                continue;
            }
            foreach (var s in g.ServicesOf(c))
            {
                (mask.ServiceVisible(s) ? services : hidden).Add(g.ServiceId(s));
            }
            foreach (var d in g.DependentsOf(c))
            {
                stack.Push(d);
            }
        }
    }

    private static string ServiceState(Cmdb.Graph.Graph g, Holds holds, long serviceId)
    {
        if (!g.TryGetService(serviceId, out var s))
        {
            return "absent";
        }
        var circuits = g.CircuitsOf(s);
        var broken = 0;
        foreach (var c in circuits)
        {
            broken += holds.Of(c) ? 0 : 1;
        }
        return broken == 0 ? "intact" : broken == circuits.Length ? "broken" : "degraded";
    }

    /// <summary>The codes the plan gives circuits it creates, by their planned id.</summary>
    private static Dictionary<long, string> PlannedCircuits(IReadOnlyList<PlanOp> ops) =>
        ops.Where(o => o.Kind == "create_circuit" && o.Payload.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            .ToDictionary(o => Planned.ObjectId(o.Id), o => o.Payload.GetProperty("code").GetString()!);

    /// <summary>Whether a circuit's path holds in a graph, memoised: physically for the physical layer, through carriers above.</summary>
    internal sealed class Holds(Cmdb.Graph.Graph g)
    {
        private readonly Dictionary<int, bool> _memo = [];

        public bool Of(int circuit)
        {
            if (_memo.TryGetValue(circuit, out var known))
            {
                return known;
            }
            _memo[circuit] = true; // a cycle of carriers does not break anything by itself
            var carriers = g.CarriersOf(circuit);
            bool holds;
            if (carriers.Length > 0)
            {
                holds = true;
                foreach (var c in carriers)
                {
                    holds &= Of(c);
                }
            }
            else
            {
                holds = Physically(circuit);
            }
            _memo[circuit] = holds;
            return holds;
        }

        /// <summary>
        /// Each hop joined to the next: by a connection or a conductor, or as two ports of the same equipment (its own
        /// internal path).
        /// </summary>
        private bool Physically(int circuit)
        {
            var hops = g.HopsOf(circuit);
            for (var i = 0; i + 1 < hops.Length; i++)
            {
                if (g.Neighbours(hops[i]).IndexOf(hops[i + 1]) < 0
                    && (g.EquipmentOf(hops[i]) is not { } e || g.EquipmentOf(hops[i + 1]) != e))
                {
                    return false;
                }
            }
            return true;
        }
    }
}

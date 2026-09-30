namespace Cmdb.Graph;

/// <summary>A circuit as its rows say now (#121): layer, hops in sequence, the circuits it rides on and its services.</summary>
public sealed record GraphCircuit(long Id, CircuitLayer Layer, IReadOnlyList<long> Hops, IReadOnlyList<long> Carriers, IReadOnlyList<long> Services);

/// <summary>
/// Circuits the change stream replaced or removed (#121), applied together: a set circuit may ride on another one set
/// in the same change.
/// </summary>
public sealed record GraphCircuitsChange(IReadOnlyList<GraphCircuit> Set, IReadOnlyList<long> Removed) : GraphChange;

/// <summary>
/// What a delta changes in the circuit arrays (#121): circuits whose hops, carriers or services were replaced, new
/// circuits and services indexed after the arrays' own, removed circuits, and the inverse lists (circuits per node,
/// dependents per carrier, circuits per service) of everything they touch. Every other circuit reads the arrays.
/// </summary>
internal sealed class CircuitOverlay
{
    public List<long> CircuitIds { get; init; } = [];
    public Dictionary<long, int> CircuitById { get; init; } = [];
    public Dictionary<int, CircuitLayer> Layers { get; init; } = [];
    public HashSet<int> Removed { get; init; } = [];
    public Dictionary<int, int[]> Hops { get; init; } = [];
    public Dictionary<int, int[]> NodeCircuits { get; init; } = [];
    public Dictionary<int, int[]> Dependents { get; init; } = [];
    public Dictionary<int, int[]> Carriers { get; init; } = [];
    public Dictionary<int, int[]> Services { get; init; } = [];
    public Dictionary<int, int[]> ServiceCircuits { get; init; } = [];
    public List<long> ServiceIds { get; init; } = [];
    public Dictionary<long, int> ServiceById { get; init; } = [];

    public CircuitOverlay Copy() => new()
    {
        CircuitIds = [.. CircuitIds],
        CircuitById = new(CircuitById),
        Layers = new(Layers),
        Removed = [.. Removed],
        Hops = new(Hops),
        NodeCircuits = new(NodeCircuits),
        Dependents = new(Dependents),
        Carriers = new(Carriers),
        Services = new(Services),
        ServiceCircuits = new(ServiceCircuits),
        ServiceIds = [.. ServiceIds],
        ServiceById = new(ServiceById),
    };
}

public sealed partial class Graph
{
    private CircuitOverlay? CircuitDelta => _overlay?.Circuits;

    /// <summary>
    /// Replaces and removes circuits in this view's delta, keeping the inverse lists in step. Nothing is applied when a
    /// hop names a terminal that does not exist, a carrier a circuit that does not, or a removed circuit still carries
    /// one that stays.
    /// </summary>
    private void ApplyCircuits(GraphOverlay overlay, GraphCircuitsChange change, int index, List<GraphChangeIssue> issues)
    {
        var setIds = change.Set.Select(c => c.Id).ToHashSet();
        var removedIds = change.Removed.ToHashSet();
        foreach (var circuit in change.Set)
        {
            if (circuit.Hops.Any(t => !TryGetNode(t, out _)))
            {
                issues.Add(new(index, change, GraphChangeProblem.UnknownTerminal));
                return;
            }
            if (circuit.Carriers.Any(c => removedIds.Contains(c) || (!setIds.Contains(c) && !TryGetCircuit(c, out _))))
            {
                issues.Add(new(index, change, GraphChangeProblem.UnknownCircuit));
                return;
            }
        }

        var delta = overlay.Circuits ??= new CircuitOverlay();

        // Indexes first, so circuits can ride on ones new in the same change.
        var indexes = new Dictionary<long, int>();
        foreach (var circuit in change.Set)
        {
            indexes[circuit.Id] = CircuitIndex(delta, circuit.Id);
        }

        foreach (var circuit in change.Set)
        {
            var c = indexes[circuit.Id];
            delta.Layers[c] = circuit.Layer;
            var hops = circuit.Hops.Select(t => TryGetNode(t, out var node) ? node : -1).ToArray();
            SetHops(delta, c, hops);
            var carriers = circuit.Carriers.Select(id => indexes.TryGetValue(id, out var i) ? i : CircuitIndexOf(id)).Distinct().Order().ToArray();
            Relink(delta.Carriers, delta.Dependents, c, CarriersOf(c), carriers, DependentsOf);
            var services = circuit.Services.Distinct().Select(id => ServiceIndex(delta, id)).Order().ToArray();
            Relink(delta.Services, delta.ServiceCircuits, c, ServicesOf(c), services, CircuitsOf);
        }

        foreach (var id in change.Removed)
        {
            if (!TryGetCircuit(id, out var c))
            {
                continue;
            }
            SetHops(delta, c, []);
            Relink(delta.Carriers, delta.Dependents, c, CarriersOf(c), [], DependentsOf);
            Relink(delta.Services, delta.ServiceCircuits, c, ServicesOf(c), [], CircuitsOf);
            if (DependentsOf(c).Length > 0)
            {
                // A circuit that stays still rides on this one; the rows do not fit together yet.
                issues.Add(new(index, change, GraphChangeProblem.UnknownCircuit));
                return;
            }
            delta.Removed.Add(c);
        }
    }

    private int CircuitIndexOf(long id) => TryGetCircuit(id, out var c) ? c : throw new InvalidOperationException($"Circuit {id} does not exist.");

    /// <summary>The circuit's index: in the arrays (brought back if it was removed), or a new one after them.</summary>
    private int CircuitIndex(CircuitOverlay delta, long id)
    {
        var c = Array.BinarySearch(CircuitIds, id);
        if (c < 0 && delta.CircuitById.TryGetValue(id, out var added))
        {
            c = CircuitIds.Length + added;
        }
        if (c >= 0)
        {
            delta.Removed.Remove(c);
            return c;
        }
        delta.CircuitById[id] = delta.CircuitIds.Count;
        delta.CircuitIds.Add(id);
        c = CircuitIds.Length + delta.CircuitIds.Count - 1;
        delta.Hops[c] = [];
        delta.Carriers[c] = [];
        delta.Services[c] = [];
        return c;
    }

    private int ServiceIndex(CircuitOverlay delta, long id)
    {
        var s = Array.BinarySearch(ServiceIds, id);
        if (s >= 0)
        {
            return s;
        }
        if (!delta.ServiceById.TryGetValue(id, out var added))
        {
            added = delta.ServiceIds.Count;
            delta.ServiceById[id] = added;
            delta.ServiceIds.Add(id);
            delta.ServiceCircuits[ServiceIds.Length + added] = [];
        }
        return ServiceIds.Length + added;
    }

    /// <summary>New hops for a circuit, and the circuit moved between the nodes' circuit lists.</summary>
    private void SetHops(CircuitOverlay delta, int circuit, int[] hops)
    {
        var before = HopsOf(circuit).ToArray().ToHashSet();
        var after = hops.ToHashSet();
        delta.Hops[circuit] = hops;
        foreach (var node in before.Except(after))
        {
            delta.NodeCircuits[node] = [.. CircuitsThrough(node).ToArray().Where(c => c != circuit)];
        }
        foreach (var node in after.Except(before))
        {
            delta.NodeCircuits[node] = [.. CircuitsThrough(node).ToArray().Append(circuit).Order()];
        }
    }

    /// <summary>
    /// A relation from <paramref name="circuit"/> (its carriers, or its services) set to <paramref name="after"/>, with
    /// the inverse lists (dependents per carrier, circuits per service) updated for what was dropped and added.
    /// </summary>
    private static void Relink(Dictionary<int, int[]> forward, Dictionary<int, int[]> inverse, int circuit, ReadOnlySpan<int> before,
        int[] after, Func<int, ReadOnlySpan<int>> current)
    {
        var old = before.ToArray().ToHashSet();
        forward[circuit] = after;
        foreach (var target in old.Except(after))
        {
            inverse[target] = [.. current(target).ToArray().Where(c => c != circuit)];
        }
        foreach (var target in after.Except(old))
        {
            inverse[target] = [.. current(target).ToArray().Append(circuit).Order()];
        }
    }
}

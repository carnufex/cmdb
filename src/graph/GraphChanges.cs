namespace Cmdb.Graph;

/// <summary>What changed since a watermark, as keys whose rows must be replaced (#11).</summary>
public sealed class GraphKeys
{
    public HashSet<long> Equipment { get; } = [];
    public HashSet<long> Cables { get; } = [];

    /// <summary>Terminals whose connections changed.</summary>
    public HashSet<long> Terminals { get; } = [];
    public HashSet<long> Circuits { get; } = [];

    public int Count => Equipment.Count + Cables.Count + Terminals.Count + Circuits.Count;
}

/// <param name="Watermark">The position after this batch; pass it to the next read. Opaque to callers.</param>
/// <param name="Reload">A bulk load happened: rebuild from scratch instead of patching.</param>
/// <param name="Keys">The changed keys.</param>
/// <param name="Rows">The current rows for <paramref name="Keys"/>: equipment and its ports, cables with conductors and ends,
/// connections touching the terminals, circuits with hops, dependencies and services.</param>
/// <param name="Changes">Outbox entries read, for diagnostics.</param>
public sealed record GraphChangeBatch(string Watermark, bool Reload, GraphKeys Keys, GraphData Rows, int Changes);

/// <summary>
/// The change stream as the graph engine sees it. Postgres (outbox + LISTEN/NOTIFY) today; a Kafka topic can take its
/// place later, with the watermark as the offset.
/// </summary>
public interface IGraphChangeFeed
{
    /// <summary>The current position: a full load started now reflects every change before it.</summary>
    Task<string> PositionAsync(CancellationToken ct);

    /// <summary>Changes committed since <paramref name="watermark"/>, with the current rows for their keys.</summary>
    Task<GraphChangeBatch> ReadAsync(string watermark, CancellationToken ct);

    /// <summary>Completes when there may be new changes, or after <paramref name="timeout"/> at the latest.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Applying a batch, in one of two ways (#81). A batch that moves connections, changes equipment and cables (#119, #123)
/// or circuits (#121) becomes a delta on the current graph: its cost follows the batch, not the network.
/// A delta is folded into the arrays now and then (<see cref="Flatten"/>). A batch whose rows do not fit the delta
/// rebuilds from rows: the base's rows with every batch since replaced in order (<see cref="Compact"/>).
/// </summary>
public static class GraphChanges
{
    /// <summary>
    /// The graph after the batch, rebuilt. Throws <see cref="InvalidOperationException"/> when the patched rows do not
    /// fit together (a later change is not visible yet); the caller then reloads in full.
    /// </summary>
    public static Graph Apply(Graph graph, GraphChangeBatch batch) => Compact(graph, [batch]);

    /// <summary>A graph without delta with the batches applied in order, rebuilt once.</summary>
    public static Graph Compact(Graph baseGraph, IReadOnlyList<GraphChangeBatch> batches)
    {
        ArgumentOutOfRangeException.ThrowIfZero(batches.Count);
        var data = GraphData.From(baseGraph);
        foreach (var batch in batches)
        {
            data.Replace(batch.Keys, batch.Rows);
        }
        return GraphBuilder.Build(data, batches[^1].Watermark);
    }

    /// <summary>
    /// A production graph's delta folded into its arrays: the adjacency is rebuilt, new objects sorted in and removed
    /// ones dropped, and what the delta does not touch is shared while indexes stay (#123). <paramref name="batches"/>
    /// are the batches applied as the delta since the arrays were built, in order.
    /// </summary>
    public static Graph Flatten(Graph graph, IReadOnlyList<GraphChangeBatch> batches) => graph.Flatten(batches);

    /// <summary>
    /// The graph after the batch as a delta, or null when it has to be rebuilt from rows: rows that do not fit, a removed
    /// terminal a circuit still passes, or a terminal that changes owner while keeping connections the batch did not read.
    /// New equipment and cables (#119) join the delta like a plan's planned objects, with indexes after the base's own;
    /// new, changed and removed circuits (#121) replace theirs; removed, moved and rebuilt equipment and cables (#123)
    /// are marked in the delta and leave the arrays when it is folded in.
    /// </summary>
    public static Graph? TryDelta(Graph graph, GraphChangeBatch batch)
    {
        var keys = batch.Keys;
        var rows = batch.Rows;

        // Structure first, removals before additions: a terminal may move from one object to another in the same batch.
        var removals = new List<GraphChange>();
        var additions = new List<GraphChange>();
        if (!Equipment(graph, keys.Equipment, rows, removals, additions) || !Cables(graph, keys.Cables, rows, removals, additions))
        {
            return null;
        }
        var view = graph;
        if (removals.Count + additions.Count > 0)
        {
            (view, var issues) = graph.WithChanges([.. removals, .. additions]);
            if (issues.Count > 0 || !OwnersChangedSafely(graph, view, keys.Terminals, additions))
            {
                return null;
            }
        }

        // Then connections and circuits, against the new structure.
        if (ConnectionChanges(view, keys.Terminals, rows) is not { } changes)
        {
            return null;
        }
        if (keys.Circuits.Count > 0)
        {
            changes.Add(Circuits(view, keys.Circuits, rows));
        }
        if (changes.Count > 0 || view == graph)
        {
            (view, var issues) = view.WithChanges(changes);
            if (issues.Count > 0)
            {
                return null;
            }
        }
        return view.CarriesCircuitsOnRemovedNodes() ? null : view.AsProduction(batch.Watermark);
    }

    /// <summary>
    /// A terminal removed and added again (a port to other equipment, an end to another conductor) comes back without
    /// connections. That is right only when the batch read its connections again, or it had none.
    /// </summary>
    private static bool OwnersChangedSafely(Graph graph, Graph view, HashSet<long> terminals, List<GraphChange> additions)
    {
        foreach (var change in additions)
        {
            IEnumerable<long> added = change switch
            {
                GraphAddPorts ports => ports.Ports,
                GraphAddConductor conductor => [conductor.Conductor.EndA, conductor.Conductor.EndB],
                GraphAddEnds ends => ends.Ends,
                GraphNewEquipment equipment => equipment.Ports,
                GraphNewCable cable => cable.Conductors.SelectMany(c => (long[])[c.EndA, c.EndB]),
                _ => [],
            };
            foreach (var id in added)
            {
                if (!terminals.Contains(id) && graph.TryGetNode(id, out var old) && view.TryGetNode(id, out var now) && old != now
                    && graph.NeighbourKinds(old).ToArray().Any(k => k != EdgeKind.Conductor))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>The changed circuits as their rows say now; a circuit without a row is removed.</summary>
    private static GraphCircuitsChange Circuits(Graph graph, HashSet<long> keys, GraphData rows)
    {
        var layers = new Dictionary<long, CircuitLayer>();
        for (var i = 0; i < rows.CircuitIds.Count; i++)
        {
            layers[rows.CircuitIds[i]] = (CircuitLayer)rows.CircuitLayers[i];
        }
        // Hops arrive ordered by circuit and sequence.
        var hops = new Dictionary<long, List<long>>();
        for (var i = 0; i < rows.HopCircuits.Count; i++)
        {
            Group(hops, rows.HopCircuits[i]).Add(rows.HopTerminals[i]);
        }
        var carriers = new Dictionary<long, List<long>>();
        for (var i = 0; i < rows.DependencyCircuits.Count; i++)
        {
            Group(carriers, rows.DependencyCircuits[i]).Add(rows.DependencyCarriers[i]);
        }
        var services = new Dictionary<long, List<long>>();
        for (var i = 0; i < rows.ServiceCircuitCircuits.Count; i++)
        {
            Group(services, rows.ServiceCircuitCircuits[i]).Add(rows.ServiceCircuitServices[i]);
        }
        var set = new List<GraphCircuit>();
        var removed = new List<long>();
        foreach (var id in keys.Order())
        {
            if (layers.TryGetValue(id, out var layer))
            {
                set.Add(new GraphCircuit(id, layer, hops.GetValueOrDefault(id) ?? [], carriers.GetValueOrDefault(id) ?? [],
                    services.GetValueOrDefault(id) ?? []));
            }
            else if (graph.TryGetCircuit(id, out _))
            {
                removed.Add(id);
            }
        }
        return new GraphCircuitsChange(set, removed);
    }

    /// <summary>
    /// The structural changes that take changed equipment to its rows: removed when it has none, new when it was not in
    /// the graph, moved to another site, and ports removed and added (#119, #123).
    /// </summary>
    private static bool Equipment(Graph graph, HashSet<long> keys, GraphData rows, List<GraphChange> removals, List<GraphChange> additions)
    {
        if (keys.Count == 0)
        {
            return true;
        }
        var sites = new Dictionary<long, long>();
        for (var i = 0; i < rows.EquipmentIds.Count; i++)
        {
            sites[rows.EquipmentIds[i]] = rows.EquipmentSites[i];
        }
        var ports = new Dictionary<long, HashSet<long>>();
        for (var i = 0; i < rows.PortTerminals.Count; i++)
        {
            Group(ports, rows.PortEquipment[i]).Add(rows.PortTerminals[i]);
        }
        foreach (var id in keys.Order())
        {
            var then = ports.GetValueOrDefault(id) ?? [];
            if (!graph.TryGetEquipment(id, out var equipment))
            {
                // Not in the rows either: nothing to do.
                if (sites.TryGetValue(id, out var newSite))
                {
                    additions.Add(new GraphNewEquipment(id, newSite, then.Order().ToArray()));
                }
                continue;
            }
            if (!sites.TryGetValue(id, out var site))
            {
                removals.Add(new GraphRemoveEquipment(id));
                continue;
            }
            if (graph.SiteId(graph.SiteIndexOfEquipment(equipment)) != site)
            {
                additions.Insert(0, new GraphMoveEquipment(id, site));
            }
            var now = new HashSet<long>();
            foreach (var node in graph.PortsOf(equipment))
            {
                now.Add(graph.TerminalId(node));
            }
            removals.AddRange(now.Except(then).Order().Select(t => new GraphRemoveTerminal(t)));
            if (then.Except(now).Order().ToArray() is { Length: > 0 } added)
            {
                additions.Add(new GraphAddPorts(id, added));
            }
        }
        return true;
    }

    /// <summary>
    /// The structural changes that take a changed cable to its rows: removed when it has none, new when it was not in the
    /// graph, conductors removed and added, and ends removed and added on the conductors that stay (#119, #123). False
    /// when a conductor in the rows does not have exactly two ends, as a delta joins them.
    /// </summary>
    private static bool Cables(Graph graph, HashSet<long> keys, GraphData rows, List<GraphChange> removals, List<GraphChange> additions)
    {
        if (keys.Count == 0)
        {
            return true;
        }
        var cables = new HashSet<long>(rows.CableIds);
        var conductorCable = new Dictionary<long, long>();
        for (var i = 0; i < rows.ConductorIds.Count; i++)
        {
            conductorCable[rows.ConductorIds[i]] = rows.ConductorCables[i];
        }
        var ends = new Dictionary<long, List<long>>();
        for (var i = 0; i < rows.EndTerminals.Count; i++)
        {
            Group(ends, rows.EndConductors[i]).Add(rows.EndTerminals[i]);
        }
        foreach (var id in keys.Order())
        {
            var exists = graph.TryGetCable(id, out var cable);
            if (!cables.Contains(id))
            {
                if (exists)
                {
                    removals.Add(new GraphRemoveCable(id));
                }
                continue;
            }
            // The rows' conductors, each with its two ends.
            var then = new SortedDictionary<long, GraphNewConductor>();
            foreach (var (conductor, owner) in conductorCable)
            {
                if (owner != id)
                {
                    continue;
                }
                if (ends.GetValueOrDefault(conductor) is not { Count: 2 } pair)
                {
                    return false;
                }
                var (a, b) = (Math.Min(pair[0], pair[1]), Math.Max(pair[0], pair[1]));
                then[conductor] = new GraphNewConductor(conductor, a, b);
            }
            if (!exists)
            {
                additions.Add(new GraphNewCable(id, [.. then.Values]));
                continue;
            }
            var now = new Dictionary<long, List<long>>();
            foreach (var node in graph.EndsOf(cable))
            {
                Group(now, graph.ConductorId(graph.OwnerOf(node))).Add(graph.TerminalId(node));
            }
            foreach (var (conductor, terminals) in now.OrderBy(c => c.Key))
            {
                if (!then.TryGetValue(conductor, out var wanted))
                {
                    removals.Add(new GraphRemoveConductor(conductor));
                    continue;
                }
                removals.AddRange(terminals.Where(t => t != wanted.EndA && t != wanted.EndB).Order().Select(t => new GraphRemoveTerminal(t)));
                if (new[] { wanted.EndA, wanted.EndB }.Where(t => !terminals.Contains(t)).ToArray() is { Length: > 0 } added)
                {
                    additions.Add(new GraphAddEnds(conductor, added));
                }
            }
            foreach (var (conductor, wanted) in then)
            {
                if (!now.ContainsKey(conductor))
                {
                    additions.Add(new GraphAddConductor(id, wanted));
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The connection changes that take the graph to the rows for the changed terminals: each pair whose connections
    /// differ is removed and added again as the rows say. A terminal not in the graph has nothing to change, unless the
    /// rows connect it; then the batch does not fit and null says so.
    /// </summary>
    private static List<GraphChange>? ConnectionChanges(Graph graph, HashSet<long> keys, GraphData rows)
    {
        var wanted = new Dictionary<(long, long), List<EdgeKind>>();
        for (var i = 0; i < rows.ConnectionA.Count; i++)
        {
            var (a, b) = (rows.ConnectionA[i], rows.ConnectionB[i]);
            if (keys.Contains(a) || keys.Contains(b))
            {
                if (!graph.TryGetNode(a, out _) || !graph.TryGetNode(b, out _))
                {
                    return null;
                }
                Group(wanted, Pair(a, b)).Add((EdgeKind)rows.ConnectionKinds[i]);
            }
        }
        var current = new Dictionary<(long, long), List<EdgeKind>>();
        foreach (var id in keys)
        {
            if (!graph.TryGetNode(id, out var node))
            {
                continue;
            }
            var targets = graph.Neighbours(node);
            var kinds = graph.NeighbourKinds(node);
            for (var i = 0; i < targets.Length; i++)
            {
                var other = graph.TerminalId(targets[i]);
                // Both ends may be keys; count each connection once, from the lower one.
                if (kinds[i] != EdgeKind.Conductor && (id < other || !keys.Contains(other)))
                {
                    Group(current, Pair(id, other)).Add(kinds[i]);
                }
            }
        }

        var removes = new List<GraphChange>();
        var adds = new List<GraphChange>();
        foreach (var pair in current.Keys.Union(wanted.Keys))
        {
            var now = current.GetValueOrDefault(pair) ?? [];
            var then = wanted.GetValueOrDefault(pair) ?? [];
            now.Sort();
            then.Sort();
            if (now.SequenceEqual(then))
            {
                continue;
            }
            if (now.Count > 0)
            {
                removes.Add(new GraphEdgeChange(pair.Item1, pair.Item2, now[0], Add: false));
            }
            adds.AddRange(then.Select(kind => new GraphEdgeChange(pair.Item1, pair.Item2, kind, Add: true)));
        }
        return [.. removes, .. adds];
    }

    private static (long, long) Pair(long a, long b) => a < b ? (a, b) : (b, a);

    private static TValue Group<TKey, TValue>(Dictionary<TKey, TValue> groups, TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (!groups.TryGetValue(key, out var group))
        {
            groups[key] = group = new();
        }
        return group;
    }
}

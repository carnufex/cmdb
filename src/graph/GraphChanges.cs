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
/// Applying a batch, in one of two ways (#81). A batch that moves connections, touches equipment and cables without
/// changing their structure, adds equipment and cables (#119) or changes circuits (#121) becomes a delta on the current
/// graph: its cost follows the batch, not the network.
/// A delta is folded into the arrays now and then (<see cref="Flatten"/>). Anything else rebuilds from rows: the base's
/// rows with every batch since replaced in order (<see cref="Compact"/>).
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
    /// A production graph's delta folded into its arrays: the adjacency is rebuilt and new objects appended, the rest
    /// is shared. <paramref name="batches"/> are the batches applied as the delta since the arrays were built, in order.
    /// Null when a new object's id is below one in the arrays; then <see cref="Compact"/> is the way.
    /// </summary>
    public static Graph? Flatten(Graph graph, IReadOnlyList<GraphChangeBatch> batches) => graph.Flatten(batches);

    /// <summary>
    /// The graph after the batch as a delta, or null when it has to be rebuilt from rows: removed, moved or rebuilt
    /// equipment and cables, or rows that do not fit. New equipment and cables (#119) join the delta like a plan's
    /// planned objects, with indexes after the base's own; new, changed and removed circuits (#121) replace theirs.
    /// </summary>
    public static Graph? TryDelta(Graph graph, GraphChangeBatch batch)
    {
        var keys = batch.Keys;
        var rows = batch.Rows;
        var changes = new List<GraphChange>();
        var added = new HashSet<long>();
        if (!Equipment(graph, keys.Equipment, rows, changes, added)
            || !Cables(graph, keys.Cables, rows, changes, added)
            || ConnectionChanges(graph, keys.Terminals, rows, added) is not { } connections)
        {
            return null;
        }
        changes.AddRange(connections);
        if (keys.Circuits.Count > 0)
        {
            changes.Add(Circuits(graph, keys.Circuits, rows));
        }
        var (view, issues) = graph.WithChanges(changes);
        return issues.Count > 0 ? null : view.AsProduction(batch.Watermark);
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
    /// Changed equipment still stands at the same site with the same ports, is new (and added to
    /// <paramref name="changes"/>, its ports to <paramref name="added"/>), or was never in the graph.
    /// </summary>
    private static bool Equipment(Graph graph, HashSet<long> keys, GraphData rows, List<GraphChange> changes, HashSet<long> added)
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
            if (!graph.TryGetEquipment(id, out var equipment))
            {
                // Not in the rows either: nothing to do.
                if (sites.TryGetValue(id, out var newSite))
                {
                    var newPorts = (ports.GetValueOrDefault(id) ?? []).Order().ToArray();
                    changes.Add(new GraphNewEquipment(id, newSite, newPorts));
                    added.UnionWith(newPorts);
                }
                continue;
            }
            if (!sites.TryGetValue(id, out var site) || graph.SiteId(graph.SiteIndexOfEquipment(equipment)) != site)
            {
                return false;
            }
            var now = graph.PortsOf(equipment);
            var then = ports.GetValueOrDefault(id) ?? [];
            if (now.Length != then.Count)
            {
                return false;
            }
            foreach (var node in now)
            {
                if (!then.Contains(graph.TerminalId(node)))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// A changed cable still has the same conductors and ends, is new (with exactly two ends per conductor, as a delta
    /// joins them), or was never in the graph.
    /// </summary>
    private static bool Cables(Graph graph, HashSet<long> keys, GraphData rows, List<GraphChange> changes, HashSet<long> added)
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
        var ends = new Dictionary<long, HashSet<(long Terminal, long Conductor)>>();
        for (var i = 0; i < rows.EndTerminals.Count; i++)
        {
            var conductor = rows.EndConductors[i];
            if (conductorCable.TryGetValue(conductor, out var owner))
            {
                Group(ends, owner).Add((rows.EndTerminals[i], conductor));
            }
        }
        foreach (var id in keys.Order())
        {
            if (!graph.TryGetCable(id, out var cable))
            {
                if (cables.Contains(id) && !New(id))
                {
                    return false;
                }
                continue;
            }
            if (!cables.Contains(id))
            {
                return false;
            }
            var now = graph.EndsOf(cable);
            var then = ends.GetValueOrDefault(id) ?? [];
            if (now.Length != then.Count)
            {
                return false;
            }
            foreach (var node in now)
            {
                if (!then.Contains((graph.TerminalId(node), graph.ConductorId(graph.OwnerOf(node)))))
                {
                    return false;
                }
            }
        }
        return true;

        bool New(long id)
        {
            var conductors = new List<GraphNewConductor>();
            foreach (var group in (ends.GetValueOrDefault(id) ?? []).GroupBy(e => e.Conductor).OrderBy(g => g.Key))
            {
                var terminals = group.Select(e => e.Terminal).Order().ToArray();
                if (terminals.Length != 2)
                {
                    return false;
                }
                conductors.Add(new GraphNewConductor(group.Key, terminals[0], terminals[1]));
                added.UnionWith(terminals);
            }
            if (conductors.Count != conductorCable.Count(c => c.Value == id))
            {
                // A conductor without ends.
                return false;
            }
            changes.Add(new GraphNewCable(id, conductors));
            return true;
        }
    }

    /// <summary>
    /// The connection changes that take the graph to the rows for the changed terminals: each pair whose connections
    /// differ is removed and added again as the rows say. Null when a terminal is neither in the graph nor new.
    /// </summary>
    private static List<GraphChange>? ConnectionChanges(Graph graph, HashSet<long> keys, GraphData rows, HashSet<long> added)
    {
        var wanted = new Dictionary<(long, long), List<EdgeKind>>();
        for (var i = 0; i < rows.ConnectionA.Count; i++)
        {
            var (a, b) = (rows.ConnectionA[i], rows.ConnectionB[i]);
            if (keys.Contains(a) || keys.Contains(b))
            {
                Group(wanted, Pair(a, b)).Add((EdgeKind)rows.ConnectionKinds[i]);
            }
        }
        var current = new Dictionary<(long, long), List<EdgeKind>>();
        foreach (var id in keys)
        {
            if (!graph.TryGetNode(id, out var node))
            {
                if (added.Contains(id))
                {
                    continue;
                }
                return null;
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

namespace Cmdb.Graph;

public sealed partial class Graph
{
    /// <summary>
    /// This production graph with its delta folded into the arrays (#81, #119, #121, #123): the adjacency is rebuilt, new
    /// terminals, equipment, conductors and cables are sorted in by id, removed ones dropped, sites follow their
    /// equipment, and the circuit arrays are rebuilt when circuits or node indexes changed. While nothing was removed and
    /// new ids follow the arrays' own, objects keep their indexes and arrays the delta does not touch are shared. The
    /// result is the graph a rebuild from rows would give, byte for byte, at a fraction of the time and memory.
    /// <paramref name="batches"/> are the batches applied as the delta, in order: they carry the lifecycles the delta
    /// does not hold.
    /// </summary>
    internal Graph Flatten(IReadOnlyList<GraphChangeBatch> batches)
    {
        if (_base is not null)
        {
            throw new InvalidOperationException("A plan view cannot be folded into production.");
        }
        var o = _overlay ?? new GraphOverlay();
        var removedConductors = o.RemovedConductors;
        if (o.RemovedCables.Count > 0)
        {
            // A removed cable's conductors go with it, also any without ends, which the delta never saw.
            removedConductors = [.. removedConductors];
            for (var c = 0; c < ConductorIds.Length + o.ConductorIds.Count; c++)
            {
                if (o.RemovedCables.Contains(CableOfConductor(c)))
                {
                    removedConductors.Add(c);
                }
            }
        }
        var terminals = Renumbering.Of(TerminalIds, o.TerminalIds, o.RemovedNodes);
        var equipment = Renumbering.Of(EquipmentIds, o.EquipmentIds, o.RemovedEquipment);
        var conductors = Renumbering.Of(ConductorIds, o.ConductorIds, removedConductors);
        var cables = Renumbering.Of(CableIds, o.CableIds, o.RemovedCables);
        // A site getting its first equipment may be older than others in the graph, and one losing its last one goes.
        var sites = Renumbering.Of(SiteIds, o.SiteIds, o.RemovedSites);

        var terminalIds = terminals.Ids;
        var n = terminalIds.Length;
        var terminalKinds = terminals.Gather(TerminalKinds, o.Kinds);
        int[] terminalOwners;
        if (terminals.Appends && equipment.Appends && conductors.Appends)
        {
            terminalOwners = terminals.Gather(TerminalOwners, o.Owners);
            for (var node = TerminalIds.Length; node < n; node++)
            {
                var owner = terminalOwners[node];
                terminalOwners[node] = terminalKinds[node] == TerminalKind.Port ? equipment.New(owner) : conductors.New(owner);
            }
        }
        else
        {
            terminalOwners = new int[n];
            for (var node = 0; node < n; node++)
            {
                var owner = OwnerOf(terminals.Old(node));
                terminalOwners[node] = terminalKinds[node] == TerminalKind.Port ? equipment.New(owner) : conductors.New(owner);
            }
        }
        var equipmentIds = equipment.Ids;
        int[] equipmentSites;
        if (equipment.Appends && sites.Appends && o.MovedEquipment.Count == 0)
        {
            equipmentSites = equipment.Gather(EquipmentSites, o.EquipmentSites);
            for (var e = EquipmentIds.Length; e < equipmentSites.Length; e++)
            {
                equipmentSites[e] = sites.New(equipmentSites[e]);
            }
        }
        else
        {
            equipmentSites = new int[equipmentIds.Length];
            for (var e = 0; e < equipmentSites.Length; e++)
            {
                equipmentSites[e] = sites.New(SiteIndexOfEquipment(equipment.Old(e)));
            }
        }
        var siteIds = sites.Ids;
        var conductorIds = conductors.Ids;
        int[] conductorCables;
        if (conductors.Appends && cables.Appends)
        {
            conductorCables = conductors.Gather(ConductorCables, o.ConductorCables);
            for (var c = ConductorIds.Length; c < conductorCables.Length; c++)
            {
                conductorCables[c] = cables.New(conductorCables[c]);
            }
        }
        else
        {
            conductorCables = new int[conductorIds.Length];
            for (var c = 0; c < conductorCables.Length; c++)
            {
                conductorCables[c] = cables.New(CableOfConductor(conductors.Old(c)));
            }
        }
        var cableIds = cables.Ids;

        // The latest lifecycle of every connection and cable the batches read, and the nodes whose edges they touch.
        var connectionLifecycles = new Dictionary<(long, long, EdgeKind), Lifecycle>();
        var cableLifecycles = new Lifecycle[cableIds.Length];
        for (var c = 0; c < cableLifecycles.Length; c++)
        {
            var old = cables.Old(c);
            cableLifecycles[c] = old < CableIds.Length ? CableLifecycles[old] : Lifecycle.InService;
        }
        var touched = new HashSet<int>(o.Edges.Keys);
        foreach (var batch in batches)
        {
            var rows = batch.Rows;
            for (var i = 0; i < rows.ConnectionA.Count; i++)
            {
                var (a, b) = (rows.ConnectionA[i], rows.ConnectionB[i]);
                connectionLifecycles[(Math.Min(a, b), Math.Max(a, b), (EdgeKind)rows.ConnectionKinds[i])] = (Lifecycle)rows.ConnectionLifecycles[i];
            }
            foreach (var id in batch.Keys.Terminals)
            {
                if (TryGetNode(id, out var node))
                {
                    touched.Add(node);
                }
            }
            for (var i = 0; i < rows.CableIds.Count; i++)
            {
                var cable = Array.BinarySearch(cableIds, rows.CableIds[i]);
                if (cable >= 0)
                {
                    cableLifecycles[cable] = (Lifecycle)rows.CableLifecycles[i];
                    touched.UnionWith(EndsOf(cables.Old(cable)).ToArray());
                }
            }
        }

        var start = new int[n + 1];
        for (var node = 0; node < n; node++)
        {
            start[node + 1] = start[node] + Neighbours(terminals.Old(node)).Length;
        }
        var targets = new int[start[n]];
        var kinds = new EdgeKind[targets.Length];
        var lifecycles = new Lifecycle[targets.Length];
        for (var node = 0; node < n; node++)
        {
            var old = terminals.Old(node);
            if (old < TerminalIds.Length && !touched.Contains(old))
            {
                // Untouched nodes of the arrays point at nodes of the arrays that stay, in the same order.
                var first = EdgeStart[old];
                var count = EdgeStart[old + 1] - first;
                if (terminals.Appends)
                {
                    Array.Copy(EdgeTargets, first, targets, start[node], count);
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        targets[start[node] + i] = terminals.New(EdgeTargets[first + i]);
                    }
                }
                Array.Copy(EdgeKinds, first, kinds, start[node], count);
                Array.Copy(EdgeLifecycles, first, lifecycles, start[node], count);
                continue;
            }
            var edges = new List<(int Target, EdgeKind Kind)>();
            var neighbours = Neighbours(old);
            var neighbourKinds = NeighbourKinds(old);
            for (var i = 0; i < neighbours.Length; i++)
            {
                edges.Add((terminals.New(neighbours[i]), neighbourKinds[i]));
            }
            // The builder's order: by target, then kind.
            edges.Sort();
            var at = start[node];
            foreach (var (target, kind) in edges)
            {
                targets[at] = target;
                kinds[at] = kind;
                lifecycles[at] = kind == EdgeKind.Conductor
                    ? cableLifecycles[conductorCables[terminalOwners[node]]]
                    : ConnectionLifecycle(old, terminalIds[node], terminalIds[target], kind, connectionLifecycles);
                at++;
            }
        }

        var circuits = FlattenCircuits(n, terminals);

        var graph = new Graph
        {
            Version = Version,
            TerminalIds = terminalIds,
            TerminalKinds = terminalKinds,
            TerminalOwners = terminalOwners,
            EdgeStart = start,
            EdgeTargets = targets,
            EdgeKinds = kinds,
            EdgeLifecycles = lifecycles,
            EquipmentIds = equipmentIds,
            EquipmentSites = equipmentSites,
            SiteIds = siteIds,
            ConductorIds = conductorIds,
            ConductorCables = conductorCables,
            CableIds = cableIds,
            CableLifecycles = cableLifecycles,
            CircuitIds = circuits.CircuitIds,
            CircuitLayers = circuits.CircuitLayers,
            HopStart = circuits.HopStart,
            HopNodes = circuits.HopNodes,
            NodeCircuitStart = circuits.NodeCircuitStart,
            NodeCircuits = circuits.NodeCircuits,
            DependentStart = circuits.DependentStart,
            Dependents = circuits.Dependents,
            CircuitServiceStart = circuits.CircuitServiceStart,
            CircuitServices = circuits.CircuitServices,
            ServiceIds = circuits.ServiceIds,
            ServiceCircuitStart = circuits.ServiceCircuitStart,
            ServiceCircuitList = circuits.ServiceCircuitList,
            CarrierStart = circuits.CarrierStart,
            Carriers = circuits.Carriers,
        };
        var appends = terminals.Appends && equipment.Appends && conductors.Appends && cables.Appends && sites.Appends
            && o.MovedEquipment.Count == 0;
        return appends && AppendOwners(graph) ? graph : graph.IndexOwners();
    }

    /// <summary>
    /// The derived owner indexes of <paramref name="graph"/>, this graph folded: the arrays' own with the new equipment's
    /// ports and new cables' ends appended, and the small site index rebuilt. False when a new terminal belongs to an
    /// object of the arrays, which a delta does not do; then they are built from scratch.
    /// </summary>
    private bool AppendOwners(Graph graph)
    {
        var ports = new List<int>();
        var portOwners = new List<int>();
        var ends = new List<int>();
        var endCables = new List<int>();
        for (var node = TerminalIds.Length; node < graph.TerminalIds.Length; node++)
        {
            var owner = graph.TerminalOwners[node];
            if (graph.TerminalKinds[node] == TerminalKind.Port)
            {
                if (owner < EquipmentIds.Length)
                {
                    return false;
                }
                ports.Add(node);
                portOwners.Add(owner - EquipmentIds.Length);
            }
            else
            {
                var cable = graph.ConductorCables[owner];
                if (cable < CableIds.Length)
                {
                    return false;
                }
                ends.Add(node);
                endCables.Add(cable - CableIds.Length);
            }
        }
        (graph.EquipmentPortStart, graph.EquipmentPorts) = Appended(EquipmentPortStart, EquipmentPorts, graph.EquipmentIds.Length - EquipmentIds.Length, portOwners, ports);
        (graph.CableEndStart, graph.CableEnds) = Appended(CableEndStart, CableEnds, graph.CableIds.Length - CableIds.Length, endCables, ends);
        if (graph.EquipmentIds.Length == EquipmentIds.Length && graph.SiteIds.Length == SiteIds.Length)
        {
            (graph.SiteEquipmentStart, graph.SiteEquipment) = (SiteEquipmentStart, SiteEquipment);
        }
        else
        {
            (graph.SiteEquipmentStart, graph.SiteEquipment) = GraphBuilder.GroupStable(graph.SiteIds.Length, graph.EquipmentSites,
                [.. Enumerable.Range(0, graph.EquipmentIds.Length)]);
        }
        return true;
    }

    /// <summary>A grouped index with groups for new keys appended; the same arrays when there are none.</summary>
    private static (int[] Start, int[] Values) Appended(int[] start, int[] values, int newKeys, List<int> keys, List<int> added)
    {
        if (newKeys == 0)
        {
            return (start, values);
        }
        var (addedStart, addedValues) = GraphBuilder.GroupStable(newKeys, [.. keys], [.. added]);
        return ([.. start, .. addedStart.Skip(1).Select(s => s + values.Length)], [.. values, .. addedValues]);
    }

    /// <summary>
    /// The circuit arrays after the delta: shared when no circuit changed and nodes kept their indexes (grown to the new
    /// node count when terminals were added), otherwise rebuilt from the delta's circuits the way the builder does it
    /// (#121, #123).
    /// </summary>
    private CircuitArrays FlattenCircuits(int nodes, Renumbering terminals)
    {
        if (CircuitDelta is null && terminals.Appends)
        {
            var grown = NodeCircuitStart;
            if (nodes > TerminalIds.Length)
            {
                // New terminals carry no circuits.
                grown = new int[nodes + 1];
                Array.Copy(NodeCircuitStart, grown, NodeCircuitStart.Length);
                Array.Fill(grown, NodeCircuitStart[^1], NodeCircuitStart.Length, nodes + 1 - NodeCircuitStart.Length);
            }
            return new CircuitArrays(CircuitIds, CircuitLayers, HopStart, HopNodes, grown, NodeCircuits, DependentStart,
                Dependents, CircuitServiceStart, CircuitServices, ServiceIds, ServiceCircuitStart, ServiceCircuitList, CarrierStart, Carriers);
        }

        var alive = Enumerable.Range(0, CircuitCount).Where(c => CircuitDelta?.Removed.Contains(c) != true).ToArray();
        var ids = alive.Select(CircuitId).ToArray();
        Array.Sort(ids, alive);
        var position = new int[CircuitCount];
        for (var p = 0; p < alive.Length; p++)
        {
            position[alive[p]] = p;
        }
        var layers = alive.Select(LayerOf).ToArray();
        var hopCircuit = new List<int>(HopNodes.Length);
        var hopNode = new List<int>(HopNodes.Length);
        var carriers = new List<int>(Carriers.Length);
        var dependents = new List<int>(Carriers.Length);
        var serviceRows = new List<long>(CircuitServices.Length);
        var serviceCircuits = new List<int>(CircuitServices.Length);
        for (var p = 0; p < alive.Length; p++)
        {
            var c = alive[p];
            foreach (var hop in HopsOf(c))
            {
                hopCircuit.Add(p);
                hopNode.Add(terminals.New(hop));
            }
            foreach (var carrier in CarriersOf(c))
            {
                carriers.Add(position[carrier]);
                dependents.Add(p);
            }
            foreach (var service in ServicesOf(c))
            {
                serviceRows.Add(ServiceId(service));
                serviceCircuits.Add(p);
            }
        }
        // Circuits per node from the delta's lists, renumbered, rather than grouped again from every hop: that would take
        // two more arrays the size of the network.
        var nodeCircuitStart = new int[nodes + 1];
        for (var n = 0; n < nodes; n++)
        {
            nodeCircuitStart[n + 1] = nodeCircuitStart[n] + CircuitsThrough(terminals.Old(n)).Length;
        }
        var nodeCircuits = new int[nodeCircuitStart[nodes]];
        for (var n = 0; n < nodes; n++)
        {
            var through = CircuitsThrough(terminals.Old(n));
            var segment = nodeCircuits.AsSpan(nodeCircuitStart[n], through.Length);
            for (var i = 0; i < through.Length; i++)
            {
                segment[i] = position[through[i]];
            }
            segment.Sort();
        }
        return GraphBuilder.Circuits(nodes, ids, layers, [.. hopCircuit], [.. hopNode], [.. carriers], [.. dependents], [.. serviceRows],
            [.. serviceCircuits], (nodeCircuitStart, nodeCircuits));
    }

    /// <summary>
    /// One kind of object's indexes before folding (the arrays' own, then the delta's) and after (#123). While nothing
    /// was removed and the delta's ids all follow the arrays' own, the arrays' objects keep their indexes, the delta's
    /// follow in id order, and arrays can be shared or appended to. Otherwise survivors are merged by id into new indexes.
    /// </summary>
    private sealed class Renumbering
    {
        private readonly int _count;
        private readonly int[]? _rank;
        private readonly int[]? _inverse;
        private readonly int[]? _newOf;
        private readonly int[]? _oldOf;

        private Renumbering(long[] ids, int count, int[]? rank, int[]? inverse, int[]? newOf, int[]? oldOf)
        {
            Ids = ids;
            _count = count;
            _rank = rank;
            _inverse = inverse;
            _newOf = newOf;
            _oldOf = oldOf;
        }

        /// <summary>The ids after folding, sorted.</summary>
        public long[] Ids { get; }

        /// <summary>The arrays' objects keep their indexes and the delta's follow.</summary>
        public bool Appends => _newOf is null;

        /// <summary>The new index of an object, or -1 when it was removed.</summary>
        public int New(int old) => _newOf?[old] ?? (old < _count ? old : _count + _rank![old - _count]);

        public int Old(int index) => _oldOf?[index] ?? (index < _count ? index : _count + _inverse![index - _count]);

        public static Renumbering Of(long[] ids, List<long> added, HashSet<int> removed)
        {
            if (removed.Count == 0 && (added.Count == 0 || ids.Length == 0 || added.Min() > ids[^1]))
            {
                var rank = Order(added);
                var inverse = new int[rank.Length];
                for (var i = 0; i < rank.Length; i++)
                {
                    inverse[rank[i]] = i;
                }
                return new Renumbering(Append(ids, added, rank), ids.Length, rank, inverse, null, null);
            }
            var fresh = Enumerable.Range(0, added.Count).Where(i => !removed.Contains(ids.Length + i)).ToArray();
            Array.Sort(fresh.Select(i => added[i]).ToArray(), fresh);
            var kept = ids.Length - removed.Count(r => r < ids.Length);
            var merged = new long[kept + fresh.Length];
            var oldOf = new int[merged.Length];
            var newOf = new int[ids.Length + added.Count];
            Array.Fill(newOf, -1);
            for (int at = 0, i = 0, f = 0; at < merged.Length; at++)
            {
                while (i < ids.Length && removed.Contains(i))
                {
                    i++;
                }
                if (f < fresh.Length && (i == ids.Length || added[fresh[f]] < ids[i]))
                {
                    oldOf[at] = ids.Length + fresh[f];
                    merged[at] = added[fresh[f++]];
                }
                else
                {
                    oldOf[at] = i;
                    merged[at] = ids[i++];
                }
                newOf[oldOf[at]] = at;
            }
            return new Renumbering(merged, ids.Length, null, null, newOf, oldOf);
        }

        /// <summary>A per-object array after folding: shared or appended to while <see cref="Appends"/>.</summary>
        public T[] Gather<T>(T[] values, List<T> added)
        {
            if (Appends)
            {
                return Append(values, added, _rank!);
            }
            var result = new T[Ids.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var old = _oldOf![i];
                result[i] = old < _count ? values[old] : added[old - _count];
            }
            return result;
        }
    }

    /// <summary>For each new object in the delta's order, its rank among the new objects by id.</summary>
    private static int[] Order(List<long> ids)
    {
        var positions = Enumerable.Range(0, ids.Count).ToArray();
        Array.Sort(ids.ToArray(), positions);
        var rank = new int[ids.Count];
        for (var r = 0; r < positions.Length; r++)
        {
            rank[positions[r]] = r;
        }
        return rank;
    }

    /// <summary>The arrays' values followed by the new ones, placed by <paramref name="rank"/>; the same array when none.</summary>
    private static T[] Append<T>(T[] values, List<T> added, int[] rank)
    {
        if (added.Count == 0)
        {
            return values;
        }
        var result = new T[values.Length + added.Count];
        Array.Copy(values, result, values.Length);
        for (var i = 0; i < added.Count; i++)
        {
            result[values.Length + rank[i]] = added[i];
        }
        return result;
    }

    /// <summary>A connection's lifecycle: from its latest row, or as it was in the arrays.</summary>
    private Lifecycle ConnectionLifecycle(int old, long a, long b, EdgeKind kind, Dictionary<(long, long, EdgeKind), Lifecycle> connections)
    {
        if (connections.TryGetValue((Math.Min(a, b), Math.Max(a, b), kind), out var lifecycle))
        {
            return lifecycle;
        }
        if (old < TerminalIds.Length)
        {
            for (var i = EdgeStart[old]; i < EdgeStart[old + 1]; i++)
            {
                if (TerminalIds[EdgeTargets[i]] == b && EdgeKinds[i] == kind)
                {
                    return EdgeLifecycles[i];
                }
            }
        }
        return Lifecycle.InService;
    }
}

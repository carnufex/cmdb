namespace Cmdb.Graph;

public sealed partial class Graph
{
    /// <summary>
    /// This production graph with its delta folded into the arrays (#81, #119, #121): the adjacency is rebuilt, new
    /// terminals, equipment, conductors and cables are appended after the arrays' own, sites are sorted in, and the
    /// circuit arrays are rebuilt when circuits changed. Arrays the delta does not touch are shared. The result is the graph a rebuild from rows would give, byte
    /// for byte, at a fraction of the time and memory. <paramref name="batches"/> are the batches applied as the delta, in
    /// order: they carry the lifecycles the delta does not hold. Null when a new object's id is not above every id of its
    /// kind in the arrays, so appending would break their order; then only a rebuild from rows will do.
    /// </summary>
    internal Graph? Flatten(IReadOnlyList<GraphChangeBatch> batches)
    {
        if (_base is not null)
        {
            throw new InvalidOperationException("A plan view cannot be folded into production.");
        }
        var o = _overlay ?? new GraphOverlay();
        if (!Appendable(TerminalIds, o.TerminalIds) || !Appendable(EquipmentIds, o.EquipmentIds)
            || !Appendable(ConductorIds, o.ConductorIds) || !Appendable(CableIds, o.CableIds))
        {
            return null;
        }

        // New objects in id order: position in the delta → position after the arrays' own.
        var terminalOrder = Order(o.TerminalIds);
        var equipmentOrder = Order(o.EquipmentIds);
        var conductorOrder = Order(o.ConductorIds);
        var cableOrder = Order(o.CableIds);
        int Node(int node) => node < TerminalIds.Length ? node : TerminalIds.Length + terminalOrder[node - TerminalIds.Length];
        var terminalInverse = new int[terminalOrder.Length];
        for (var i = 0; i < terminalOrder.Length; i++)
        {
            terminalInverse[terminalOrder[i]] = i;
        }
        int Old(int node) => node < TerminalIds.Length ? node : TerminalIds.Length + terminalInverse[node - TerminalIds.Length];
        int EquipmentIndex(int e) => e < EquipmentIds.Length ? e : EquipmentIds.Length + equipmentOrder[e - EquipmentIds.Length];
        int ConductorIndex(int c) => c < ConductorIds.Length ? c : ConductorIds.Length + conductorOrder[c - ConductorIds.Length];
        int CableIndex(int c) => c < CableIds.Length ? c : CableIds.Length + cableOrder[c - CableIds.Length];

        // Sites merge: a site getting its first equipment may be older than others in the graph.
        var siteIds = SiteIds.Concat(o.SiteIds).Order().ToArray();
        var siteIndex = new int[SiteIds.Length + o.SiteIds.Count];
        for (var s = 0; s < siteIndex.Length; s++)
        {
            siteIndex[s] = Array.BinarySearch(siteIds, SiteId(s));
        }

        var terminalIds = Append(TerminalIds, o.TerminalIds, terminalOrder);
        var terminalKinds = Append(TerminalKinds, o.Kinds, terminalOrder);
        var terminalOwners = Append(TerminalOwners, o.Owners, terminalOrder);
        for (var node = TerminalIds.Length; node < terminalOwners.Length; node++)
        {
            var owner = terminalOwners[node];
            terminalOwners[node] = terminalKinds[node] == TerminalKind.Port ? EquipmentIndex(owner) : ConductorIndex(owner);
        }
        var equipmentIds = Append(EquipmentIds, o.EquipmentIds, equipmentOrder);
        var equipmentSites = Append(EquipmentSites, o.EquipmentSites, equipmentOrder);
        if (o.SiteIds.Count > 0)
        {
            // New sites move the indexes of the ones sorted after them.
            equipmentSites = [.. equipmentSites];
            for (var e = 0; e < equipmentSites.Length; e++)
            {
                equipmentSites[e] = siteIndex[equipmentSites[e]];
            }
        }
        var conductorIds = Append(ConductorIds, o.ConductorIds, conductorOrder);
        var conductorCables = Append(ConductorCables, o.ConductorCables, conductorOrder);
        for (var c = ConductorIds.Length; c < conductorCables.Length; c++)
        {
            conductorCables[c] = CableIndex(conductorCables[c]);
        }
        var cableIds = Append(CableIds, o.CableIds, cableOrder);

        // The latest lifecycle of every connection and cable the batches read, and the nodes whose edges they touch.
        var connectionLifecycles = new Dictionary<(long, long, EdgeKind), Lifecycle>();
        var cableLifecycles = new Lifecycle[cableIds.Length];
        Array.Copy(CableLifecycles, cableLifecycles, CableLifecycles.Length);
        Array.Fill(cableLifecycles, Lifecycle.InService, CableLifecycles.Length, cableIds.Length - CableLifecycles.Length);
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
                    if (cable < CableIds.Length)
                    {
                        touched.UnionWith(EndsOf(cable).ToArray());
                    }
                }
            }
        }

        var n = terminalIds.Length;
        var start = new int[n + 1];
        for (var node = 0; node < n; node++)
        {
            start[node + 1] = start[node] + Neighbours(Old(node)).Length;
        }
        var targets = new int[start[n]];
        var kinds = new EdgeKind[targets.Length];
        var lifecycles = new Lifecycle[targets.Length];
        for (var node = 0; node < n; node++)
        {
            var old = Old(node);
            if (old < TerminalIds.Length && !touched.Contains(old))
            {
                // Untouched nodes of the arrays point at nodes of the arrays, whose indexes stay.
                var first = EdgeStart[old];
                var count = EdgeStart[old + 1] - first;
                Array.Copy(EdgeTargets, first, targets, start[node], count);
                Array.Copy(EdgeKinds, first, kinds, start[node], count);
                Array.Copy(EdgeLifecycles, first, lifecycles, start[node], count);
                continue;
            }
            var edges = new List<(int Target, EdgeKind Kind)>();
            var neighbours = Neighbours(old);
            var neighbourKinds = NeighbourKinds(old);
            for (var i = 0; i < neighbours.Length; i++)
            {
                edges.Add((Node(neighbours[i]), neighbourKinds[i]));
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

        var circuits = FlattenCircuits(n, Node, Old);

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
        return AppendOwners(graph) ? graph : graph.IndexOwners();
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
    /// The circuit arrays after the delta: shared when no circuit changed (grown to the new node count when terminals
    /// were added), otherwise rebuilt from the delta's circuits the way the builder does it (#121).
    /// </summary>
    private CircuitArrays FlattenCircuits(int nodes, Func<int, int> node, Func<int, int> old)
    {
        if (CircuitDelta is null)
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

        var alive = Enumerable.Range(0, CircuitCount).Where(c => !CircuitDelta.Removed.Contains(c)).ToArray();
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
                hopNode.Add(node(hop));
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
            nodeCircuitStart[n + 1] = nodeCircuitStart[n] + CircuitsThrough(old(n)).Length;
        }
        var nodeCircuits = new int[nodeCircuitStart[nodes]];
        for (var n = 0; n < nodes; n++)
        {
            var through = CircuitsThrough(old(n));
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

    /// <summary>New ids can follow the arrays' own when they are all above the highest.</summary>
    private static bool Appendable(long[] ids, List<long> added) => added.Count == 0 || ids.Length == 0 || added.Min() > ids[^1];

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

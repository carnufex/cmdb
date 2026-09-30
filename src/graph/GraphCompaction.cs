namespace Cmdb.Graph;

public sealed partial class Graph
{
    /// <summary>
    /// This production graph with its delta folded into the arrays (#81, #119): the adjacency is rebuilt, and new
    /// terminals, equipment, conductors and cables are appended after the arrays' own, with sites sorted in. Arrays the
    /// delta does not touch (circuits, services) are shared. The result is the graph a rebuild from rows would give, byte
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
        var from = new int[n];
        for (var node = 0; node < NodeCount; node++)
        {
            from[Node(node)] = node;
        }
        var start = new int[n + 1];
        for (var node = 0; node < n; node++)
        {
            start[node + 1] = start[node] + Neighbours(from[node]).Length;
        }
        var targets = new int[start[n]];
        var kinds = new EdgeKind[targets.Length];
        var lifecycles = new Lifecycle[targets.Length];
        for (var node = 0; node < n; node++)
        {
            var old = from[node];
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

        // New terminals carry no circuits: a changed circuit is rebuilt from rows.
        var nodeCircuitStart = NodeCircuitStart;
        if (n > TerminalIds.Length)
        {
            nodeCircuitStart = new int[n + 1];
            Array.Copy(NodeCircuitStart, nodeCircuitStart, NodeCircuitStart.Length);
            Array.Fill(nodeCircuitStart, NodeCircuitStart[^1], NodeCircuitStart.Length, n + 1 - NodeCircuitStart.Length);
        }

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
            CircuitIds = CircuitIds,
            CircuitLayers = CircuitLayers,
            HopStart = HopStart,
            HopNodes = HopNodes,
            NodeCircuitStart = nodeCircuitStart,
            NodeCircuits = NodeCircuits,
            DependentStart = DependentStart,
            Dependents = Dependents,
            CircuitServiceStart = CircuitServiceStart,
            CircuitServices = CircuitServices,
            ServiceIds = ServiceIds,
            ServiceCircuitStart = ServiceCircuitStart,
            ServiceCircuitList = ServiceCircuitList,
            CarrierStart = CarrierStart,
            Carriers = Carriers,
        };
        if (o.TerminalIds.Count + o.EquipmentIds.Count + o.CableIds.Count + o.SiteIds.Count == 0)
        {
            // Nothing new: the owner indexes stay as they are.
            graph.EquipmentPortStart = EquipmentPortStart;
            graph.EquipmentPorts = EquipmentPorts;
            graph.CableEndStart = CableEndStart;
            graph.CableEnds = CableEnds;
            graph.SiteEquipmentStart = SiteEquipmentStart;
            graph.SiteEquipment = SiteEquipment;
            return graph;
        }
        return graph.IndexOwners();
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

namespace Cmdb.Graph;

public sealed partial class Graph
{
    /// <summary>
    /// This production graph with its delta folded into the arrays (#81). A production delta has no new objects, so only
    /// the adjacency and the cables' lifecycles are rebuilt; every other array is shared. The result is the graph a
    /// rebuild from rows would give (byte for byte), at a fraction of the time and memory. <paramref name="batches"/> are
    /// the batches applied as the delta, in order: they carry the lifecycles the delta does not hold.
    /// </summary>
    internal Graph Flatten(IReadOnlyList<GraphChangeBatch> batches)
    {
        if (_base is not null)
        {
            throw new InvalidOperationException("A plan view cannot be folded into production.");
        }
        if (NodeCount != TerminalIds.Length)
        {
            throw new InvalidOperationException("A production delta cannot hold new objects.");
        }

        // The latest lifecycle of every connection and cable the batches read, and the nodes whose edges they touch.
        var connectionLifecycles = new Dictionary<(long, long, EdgeKind), Lifecycle>();
        var cableLifecycles = (Lifecycle[])CableLifecycles.Clone();
        var touched = _overlay is null ? new HashSet<int>() : new HashSet<int>(_overlay.Edges.Keys);
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
                if (TryGetCable(rows.CableIds[i], out var cable))
                {
                    cableLifecycles[cable] = (Lifecycle)rows.CableLifecycles[i];
                    touched.UnionWith(EndsOf(cable).ToArray());
                }
            }
        }

        var n = TerminalIds.Length;
        var start = new int[n + 1];
        for (var node = 0; node < n; node++)
        {
            start[node + 1] = start[node] + Neighbours(node).Length;
        }
        var targets = new int[start[n]];
        var kinds = new EdgeKind[targets.Length];
        var lifecycles = new Lifecycle[targets.Length];
        for (var node = 0; node < n; node++)
        {
            var from = EdgeStart[node];
            var count = EdgeStart[node + 1] - from;
            if (!touched.Contains(node))
            {
                Array.Copy(EdgeTargets, from, targets, start[node], count);
                Array.Copy(EdgeKinds, from, kinds, start[node], count);
                Array.Copy(EdgeLifecycles, from, lifecycles, start[node], count);
                continue;
            }
            var edges = new List<(int Target, EdgeKind Kind)>();
            var neighbours = Neighbours(node);
            var neighbourKinds = NeighbourKinds(node);
            for (var i = 0; i < neighbours.Length; i++)
            {
                edges.Add((neighbours[i], neighbourKinds[i]));
            }
            // The builder's order: by target, then kind.
            edges.Sort();
            var at = start[node];
            foreach (var (target, kind) in edges)
            {
                targets[at] = target;
                kinds[at] = kind;
                lifecycles[at] = LifecycleOf(node, target, kind, from, count, connectionLifecycles, cableLifecycles);
                at++;
            }
        }

        return new Graph
        {
            Version = Version,
            TerminalIds = TerminalIds,
            TerminalKinds = TerminalKinds,
            TerminalOwners = TerminalOwners,
            EdgeStart = start,
            EdgeTargets = targets,
            EdgeKinds = kinds,
            EdgeLifecycles = lifecycles,
            EquipmentIds = EquipmentIds,
            EquipmentSites = EquipmentSites,
            SiteIds = SiteIds,
            ConductorIds = ConductorIds,
            ConductorCables = ConductorCables,
            CableIds = CableIds,
            CableLifecycles = cableLifecycles,
            CircuitIds = CircuitIds,
            CircuitLayers = CircuitLayers,
            HopStart = HopStart,
            HopNodes = HopNodes,
            NodeCircuitStart = NodeCircuitStart,
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
            EquipmentPortStart = EquipmentPortStart,
            EquipmentPorts = EquipmentPorts,
            CableEndStart = CableEndStart,
            CableEnds = CableEnds,
            SiteEquipmentStart = SiteEquipmentStart,
            SiteEquipment = SiteEquipment,
        };
    }

    /// <summary>
    /// A conductor takes its cable's lifecycle, as in the builder; a connection the one its latest row gave, or the one
    /// it had in the arrays.
    /// </summary>
    private Lifecycle LifecycleOf(int node, int target, EdgeKind kind, int from, int count,
        Dictionary<(long, long, EdgeKind), Lifecycle> connections, Lifecycle[] cables)
    {
        if (kind == EdgeKind.Conductor)
        {
            return cables[ConductorCables[TerminalOwners[node]]];
        }
        var (a, b) = (TerminalIds[node], TerminalIds[target]);
        if (connections.TryGetValue((Math.Min(a, b), Math.Max(a, b), kind), out var lifecycle))
        {
            return lifecycle;
        }
        for (var i = from; i < from + count; i++)
        {
            if (EdgeTargets[i] == target && EdgeKinds[i] == kind)
            {
                return EdgeLifecycles[i];
            }
        }
        return Lifecycle.InService;
    }
}

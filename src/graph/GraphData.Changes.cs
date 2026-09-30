namespace Cmdb.Graph;

public sealed partial class GraphData
{
    /// <summary>
    /// The rows a graph was built from, recovered from the graph itself (#11): building them again gives an identical
    /// graph. Connections come back once each, from the lower terminal.
    /// </summary>
    public static GraphData From(Graph g)
    {
        if (g.IsOverlay)
        {
            throw new ArgumentException("A graph with a delta has no rows of its own; compact it first.", nameof(g));
        }
        var d = new GraphData();
        // Sized up front: at full scale these are millions of rows, and growing by doubling would double the peak.
        var ports = 0;
        foreach (var kind in g.TerminalKinds)
        {
            ports += kind == TerminalKind.Port ? 1 : 0;
        }
        var ends = g.TerminalIds.Length - ports;
        var connections = (g.EdgeTargets.Length / 2) - g.ConductorIds.Length;
        d.EquipmentIds.Capacity = d.EquipmentSites.Capacity = g.EquipmentIds.Length;
        d.CableIds.Capacity = d.CableLifecycles.Capacity = g.CableIds.Length;
        d.ConductorIds.Capacity = d.ConductorCables.Capacity = g.ConductorIds.Length;
        d.PortTerminals.Capacity = d.PortEquipment.Capacity = ports;
        d.EndTerminals.Capacity = d.EndConductors.Capacity = ends;
        d.ConnectionA.Capacity = d.ConnectionB.Capacity = d.ConnectionKinds.Capacity = d.ConnectionLifecycles.Capacity = Math.Max(connections, 0);
        d.CircuitIds.Capacity = d.CircuitLayers.Capacity = g.CircuitIds.Length;
        d.HopCircuits.Capacity = d.HopTerminals.Capacity = g.HopNodes.Length;
        d.DependencyCircuits.Capacity = d.DependencyCarriers.Capacity = g.Dependents.Length;
        d.ServiceCircuitServices.Capacity = d.ServiceCircuitCircuits.Capacity = g.CircuitServices.Length;
        for (var e = 0; e < g.EquipmentIds.Length; e++)
        {
            d.EquipmentIds.Add(g.EquipmentIds[e]);
            d.EquipmentSites.Add(g.SiteIds[g.EquipmentSites[e]]);
        }
        for (var c = 0; c < g.CableIds.Length; c++)
        {
            d.CableIds.Add(g.CableIds[c]);
            d.CableLifecycles.Add((byte)g.CableLifecycles[c]);
        }
        for (var c = 0; c < g.ConductorIds.Length; c++)
        {
            d.ConductorIds.Add(g.ConductorIds[c]);
            d.ConductorCables.Add(g.CableIds[g.ConductorCables[c]]);
        }
        for (var node = 0; node < g.TerminalIds.Length; node++)
        {
            if (g.TerminalKinds[node] == TerminalKind.Port)
            {
                d.PortTerminals.Add(g.TerminalIds[node]);
                d.PortEquipment.Add(g.EquipmentIds[g.TerminalOwners[node]]);
            }
            else
            {
                d.EndTerminals.Add(g.TerminalIds[node]);
                d.EndConductors.Add(g.ConductorIds[g.TerminalOwners[node]]);
            }
            for (var i = g.EdgeStart[node]; i < g.EdgeStart[node + 1]; i++)
            {
                var target = g.EdgeTargets[i];
                if (g.EdgeKinds[i] != EdgeKind.Conductor && node < target)
                {
                    d.ConnectionA.Add(g.TerminalIds[node]);
                    d.ConnectionB.Add(g.TerminalIds[target]);
                    d.ConnectionKinds.Add((byte)g.EdgeKinds[i]);
                    d.ConnectionLifecycles.Add((byte)g.EdgeLifecycles[i]);
                }
            }
        }
        for (var c = 0; c < g.CircuitIds.Length; c++)
        {
            var id = g.CircuitIds[c];
            d.CircuitIds.Add(id);
            d.CircuitLayers.Add((byte)g.CircuitLayers[c]);
            foreach (var node in g.HopsOf(c))
            {
                d.HopCircuits.Add(id);
                d.HopTerminals.Add(g.TerminalIds[node]);
            }
            foreach (var dependent in g.DependentsOf(c))
            {
                d.DependencyCircuits.Add(g.CircuitIds[dependent]);
                d.DependencyCarriers.Add(id);
            }
            foreach (var service in g.ServicesOf(c))
            {
                d.ServiceCircuitServices.Add(g.ServiceIds[service]);
                d.ServiceCircuitCircuits.Add(id);
            }
        }
        return d;
    }

    /// <summary>Replaces every row belonging to a changed key with that key's current rows.</summary>
    public void Replace(GraphKeys keys, GraphData current)
    {
        if (keys.Equipment.Count > 0)
        {
            Keep(Mask(EquipmentIds, keys.Equipment), EquipmentIds, EquipmentSites);
            Keep(Mask(PortEquipment, keys.Equipment), PortTerminals, PortEquipment);
        }
        if (keys.Cables.Count > 0)
        {
            var conductors = new HashSet<long>();
            for (var i = 0; i < ConductorIds.Count; i++)
            {
                if (keys.Cables.Contains(ConductorCables[i]))
                {
                    conductors.Add(ConductorIds[i]);
                }
            }
            Keep(Mask(CableIds, keys.Cables), CableIds, CableLifecycles);
            Keep(Mask(ConductorCables, keys.Cables), ConductorIds, ConductorCables);
            Keep(Mask(EndConductors, conductors), EndTerminals, EndConductors);
        }
        if (keys.Terminals.Count > 0)
        {
            var keep = Mask(ConnectionA, keys.Terminals);
            var keepB = Mask(ConnectionB, keys.Terminals);
            for (var i = 0; i < keep.Length; i++)
            {
                keep[i] &= keepB[i];
            }
            Keep(keep, ConnectionA, ConnectionB, ConnectionKinds, ConnectionLifecycles);
        }
        if (keys.Circuits.Count > 0)
        {
            Keep(Mask(CircuitIds, keys.Circuits), CircuitIds, CircuitLayers);
            Keep(Mask(HopCircuits, keys.Circuits), HopCircuits, HopTerminals);
            Keep(Mask(DependencyCircuits, keys.Circuits), DependencyCircuits, DependencyCarriers);
            Keep(Mask(ServiceCircuitCircuits, keys.Circuits), ServiceCircuitServices, ServiceCircuitCircuits);
        }

        EquipmentIds.AddRange(current.EquipmentIds);
        EquipmentSites.AddRange(current.EquipmentSites);
        PortTerminals.AddRange(current.PortTerminals);
        PortEquipment.AddRange(current.PortEquipment);
        CableIds.AddRange(current.CableIds);
        CableLifecycles.AddRange(current.CableLifecycles);
        ConductorIds.AddRange(current.ConductorIds);
        ConductorCables.AddRange(current.ConductorCables);
        EndTerminals.AddRange(current.EndTerminals);
        EndConductors.AddRange(current.EndConductors);
        ConnectionA.AddRange(current.ConnectionA);
        ConnectionB.AddRange(current.ConnectionB);
        ConnectionKinds.AddRange(current.ConnectionKinds);
        ConnectionLifecycles.AddRange(current.ConnectionLifecycles);
        CircuitIds.AddRange(current.CircuitIds);
        CircuitLayers.AddRange(current.CircuitLayers);
        HopCircuits.AddRange(current.HopCircuits);
        HopTerminals.AddRange(current.HopTerminals);
        DependencyCircuits.AddRange(current.DependencyCircuits);
        DependencyCarriers.AddRange(current.DependencyCarriers);
        ServiceCircuitServices.AddRange(current.ServiceCircuitServices);
        ServiceCircuitCircuits.AddRange(current.ServiceCircuitCircuits);
    }

    /// <summary>True for the rows to keep: those whose key is not among the changed ones.</summary>
    private static bool[] Mask(List<long> column, HashSet<long> changed)
    {
        var keep = new bool[column.Count];
        for (var i = 0; i < keep.Length; i++)
        {
            keep[i] = !changed.Contains(column[i]);
        }
        return keep;
    }

    private static void Keep<TA, TB>(bool[] keep, List<TA> a, List<TB> b)
    {
        Compact(keep, a);
        Compact(keep, b);
    }

    private static void Keep<TA, TB, TC, TD>(bool[] keep, List<TA> a, List<TB> b, List<TC> c, List<TD> d)
    {
        Compact(keep, a);
        Compact(keep, b);
        Compact(keep, c);
        Compact(keep, d);
    }

    private static void Compact<T>(bool[] keep, List<T> list)
    {
        var to = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (keep[i])
            {
                list[to++] = list[i];
            }
        }
        list.RemoveRange(to, list.Count - to);
    }
}

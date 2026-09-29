namespace Cmdb.Graph;

/// <summary>
/// Builds a <see cref="Graph"/> from rows. Everything is sorted by external id and adjacency lists are sorted by target,
/// so the same data always gives byte-identical arrays, whatever order the rows arrived in.
/// </summary>
public static class GraphBuilder
{
    public static Graph Build(GraphData data, string version)
    {
        // Terminals: ports and conductor ends together, sorted by id.
        var portCount = data.PortTerminals.Count;
        var n = portCount + data.EndTerminals.Count;
        var terminalIds = new long[n];
        var order = new int[n];
        for (var i = 0; i < portCount; i++)
        {
            terminalIds[i] = data.PortTerminals[i];
        }
        for (var i = 0; i < data.EndTerminals.Count; i++)
        {
            terminalIds[portCount + i] = data.EndTerminals[i];
        }
        for (var i = 0; i < n; i++)
        {
            order[i] = i;
        }
        Array.Sort(terminalIds, order);

        var equipmentIds = data.EquipmentIds.ToArray();
        var equipmentSiteIds = data.EquipmentSites.ToArray();
        Array.Sort(equipmentIds, equipmentSiteIds);
        var siteIds = equipmentSiteIds.Distinct().Order().ToArray();
        var equipmentSites = Map(equipmentSiteIds, siteIds, "equipment site");

        var conductorIds = data.ConductorIds.ToArray();
        var conductorCableIds = data.ConductorCables.ToArray();
        Array.Sort(conductorIds, conductorCableIds);
        var cableIds = data.CableIds.ToArray();
        var cableLifecycles = data.CableLifecycles.Select(l => (Lifecycle)l).ToArray();
        Array.Sort(cableIds, cableLifecycles);
        var conductorCables = Map(conductorCableIds, cableIds, "conductor cable");

        var kinds = new TerminalKind[n];
        var owners = new int[n];
        for (var node = 0; node < n; node++)
        {
            var source = order[node];
            if (source < portCount)
            {
                kinds[node] = TerminalKind.Port;
                owners[node] = Find(equipmentIds, data.PortEquipment[source], "port equipment");
            }
            else
            {
                kinds[node] = TerminalKind.ConductorEnd;
                owners[node] = Find(conductorIds, data.EndConductors[source - portCount], "conductor end");
            }
        }

        // Edges: connections, then one per conductor between its two ends.
        var edgeA = new List<int>(data.ConnectionA.Count + conductorIds.Length);
        var edgeB = new List<int>(edgeA.Capacity);
        var edgeKind = new List<EdgeKind>(edgeA.Capacity);
        var edgeLifecycle = new List<Lifecycle>(edgeA.Capacity);
        for (var i = 0; i < data.ConnectionA.Count; i++)
        {
            edgeA.Add(Find(terminalIds, data.ConnectionA[i], "connection a"));
            edgeB.Add(Find(terminalIds, data.ConnectionB[i], "connection b"));
            edgeKind.Add((EdgeKind)data.ConnectionKinds[i]);
            edgeLifecycle.Add((Lifecycle)data.ConnectionLifecycles[i]);
        }
        var firstEnd = new int[conductorIds.Length];
        Array.Fill(firstEnd, -1);
        for (var node = 0; node < n; node++)
        {
            if (kinds[node] != TerminalKind.ConductorEnd)
            {
                continue;
            }
            var conductor = owners[node];
            if (firstEnd[conductor] < 0)
            {
                firstEnd[conductor] = node;
            }
            else
            {
                edgeA.Add(firstEnd[conductor]);
                edgeB.Add(node);
                edgeKind.Add(EdgeKind.Conductor);
                edgeLifecycle.Add(cableLifecycles[conductorCables[conductor]]);
            }
        }

        var (edgeStart, edgeTargets, edgeKinds, edgeLifecycles) = Adjacency(n, edgeA, edgeB, edgeKind, edgeLifecycle);

        // Circuits and their hops (rows arrive ordered by circuit and sequence, but circuits need not be sorted).
        var circuitIds = data.CircuitIds.ToArray();
        var circuitLayers = data.CircuitLayers.Select(l => (CircuitLayer)l).ToArray();
        Array.Sort(circuitIds, circuitLayers);
        var hopCircuit = new int[data.HopCircuits.Count];
        var hopNode = new int[data.HopCircuits.Count];
        for (var i = 0; i < hopCircuit.Length; i++)
        {
            hopCircuit[i] = Find(circuitIds, data.HopCircuits[i], "hop circuit");
            hopNode[i] = Find(terminalIds, data.HopTerminals[i], "hop terminal");
        }
        var (hopStart, hopNodes) = GroupStable(circuitIds.Length, hopCircuit, hopNode);
        var (nodeCircuitStart, nodeCircuits) = GroupDistinct(n, hopNode, hopCircuit);

        var dependents = data.DependencyCarriers.Select(c => Find(circuitIds, c, "carrier")).ToArray();
        var dependentCircuits = data.DependencyCircuits.Select(c => Find(circuitIds, c, "dependent")).ToArray();
        var (dependentStart, dependentList) = GroupDistinct(circuitIds.Length, dependents, dependentCircuits);

        var serviceIds = data.ServiceCircuitServices.Distinct().Order().ToArray();
        var serviceCircuits = data.ServiceCircuitCircuits.Select(c => Find(circuitIds, c, "service circuit")).ToArray();
        var services = data.ServiceCircuitServices.Select(s => Array.BinarySearch(serviceIds, s)).ToArray();
        var (circuitServiceStart, circuitServices) = GroupDistinct(circuitIds.Length, serviceCircuits, services);
        var (serviceCircuitStart, serviceCircuitList) = GroupDistinct(serviceIds.Length, services, serviceCircuits);
        var (carrierStart, carrierList) = GroupDistinct(circuitIds.Length, dependentCircuits, dependents);

        return new Graph
        {
            Version = version,
            TerminalIds = terminalIds,
            TerminalKinds = kinds,
            TerminalOwners = owners,
            EdgeStart = edgeStart,
            EdgeTargets = edgeTargets,
            EdgeKinds = edgeKinds,
            EdgeLifecycles = edgeLifecycles,
            EquipmentIds = equipmentIds,
            EquipmentSites = equipmentSites,
            SiteIds = siteIds,
            ConductorIds = conductorIds,
            ConductorCables = conductorCables,
            CableIds = cableIds,
            CableLifecycles = cableLifecycles,
            CircuitIds = circuitIds,
            CircuitLayers = circuitLayers,
            HopStart = hopStart,
            HopNodes = hopNodes,
            NodeCircuitStart = nodeCircuitStart,
            NodeCircuits = nodeCircuits,
            DependentStart = dependentStart,
            Dependents = dependentList,
            CircuitServiceStart = circuitServiceStart,
            CircuitServices = circuitServices,
            ServiceIds = serviceIds,
            ServiceCircuitStart = serviceCircuitStart,
            ServiceCircuitList = serviceCircuitList,
            CarrierStart = carrierStart,
            Carriers = carrierList,
        };
    }

    /// <summary>Both directions of every edge, grouped per node with counting sort and sorted by target.</summary>
    private static (int[] Start, int[] Targets, EdgeKind[] Kinds, Lifecycle[] Lifecycles) Adjacency(
        int n, List<int> a, List<int> b, List<EdgeKind> kind, List<Lifecycle> lifecycle)
    {
        var start = new int[n + 1];
        for (var i = 0; i < a.Count; i++)
        {
            start[a[i] + 1]++;
            start[b[i] + 1]++;
        }
        for (var i = 0; i < n; i++)
        {
            start[i + 1] += start[i];
        }
        var fill = (int[])start.Clone();
        var targets = new int[a.Count * 2];
        var kinds = new EdgeKind[targets.Length];
        var lifecycles = new Lifecycle[targets.Length];
        for (var i = 0; i < a.Count; i++)
        {
            var p = fill[a[i]]++;
            targets[p] = b[i];
            kinds[p] = kind[i];
            lifecycles[p] = lifecycle[i];
            p = fill[b[i]]++;
            targets[p] = a[i];
            kinds[p] = kind[i];
            lifecycles[p] = lifecycle[i];
        }
        // Deterministic order within each node; degrees are tiny, so insertion sort.
        for (var node = 0; node < n; node++)
        {
            for (var i = start[node] + 1; i < start[node + 1]; i++)
            {
                var (t, k, l) = (targets[i], kinds[i], lifecycles[i]);
                var j = i - 1;
                while (j >= start[node] && (targets[j] > t || (targets[j] == t && kinds[j] > k)))
                {
                    targets[j + 1] = targets[j];
                    kinds[j + 1] = kinds[j];
                    lifecycles[j + 1] = lifecycles[j];
                    j--;
                }
                targets[j + 1] = t;
                kinds[j + 1] = k;
                lifecycles[j + 1] = l;
            }
        }
        return (start, targets, kinds, lifecycles);
    }

    /// <summary>Groups values by key keeping input order (hops stay in sequence).</summary>
    private static (int[] Start, int[] Values) GroupStable(int keys, int[] key, int[] value)
    {
        var start = new int[keys + 1];
        foreach (var k in key)
        {
            start[k + 1]++;
        }
        for (var i = 0; i < keys; i++)
        {
            start[i + 1] += start[i];
        }
        var fill = (int[])start.Clone();
        var values = new int[key.Length];
        for (var i = 0; i < key.Length; i++)
        {
            values[fill[key[i]]++] = value[i];
        }
        return (start, values);
    }

    /// <summary>Groups values by key, each group sorted and without duplicates.</summary>
    private static (int[] Start, int[] Values) GroupDistinct(int keys, int[] key, int[] value)
    {
        var (start, values) = GroupStable(keys, key, value);
        var outStart = new int[keys + 1];
        var outValues = new List<int>(values.Length);
        for (var k = 0; k < keys; k++)
        {
            var group = values.AsSpan(start[k], start[k + 1] - start[k]);
            group.Sort();
            for (var i = 0; i < group.Length; i++)
            {
                if (i == 0 || group[i] != group[i - 1])
                {
                    outValues.Add(group[i]);
                }
            }
            outStart[k + 1] = outValues.Count;
        }
        return (outStart, outValues.ToArray());
    }

    private static int[] Map(long[] ids, long[] sortedTargets, string what)
    {
        var result = new int[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            result[i] = Find(sortedTargets, ids[i], what);
        }
        return result;
    }

    private static int Find(long[] sorted, long id, string what)
    {
        var i = Array.BinarySearch(sorted, id);
        return i >= 0 ? i : throw new InvalidOperationException($"Graph data is inconsistent: {what} {id} does not exist.");
    }
}

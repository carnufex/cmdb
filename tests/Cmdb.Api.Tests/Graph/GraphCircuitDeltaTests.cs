using Cmdb.Graph;

namespace Cmdb.Api.Tests.Graph;

/// <summary>
/// Circuits from the change stream as a delta (#121): new, rerouted and removed circuits answer like a graph rebuilt
/// from rows, and folding the delta in gives that graph byte for byte.
/// </summary>
public sealed class GraphCircuitDeltaTests
{
    private const int Ports = 60;

    private sealed record Circuit(CircuitLayer Layer, long[] Hops, long[] Carriers, long[] Services);

    /// <summary>Ports 1..60 on six pieces of equipment, patched in pairs, and the circuits as rows.</summary>
    private static GraphData Network(SortedDictionary<long, Circuit> circuits)
    {
        var d = new GraphData();
        for (var e = 0; e < 6; e++)
        {
            d.EquipmentIds.Add(100 + e);
            d.EquipmentSites.Add(1 + (e % 2));
        }
        for (var p = 1; p <= Ports; p++)
        {
            d.PortTerminals.Add(p);
            d.PortEquipment.Add(100 + ((p - 1) % 6));
        }
        for (var p = 1; p < Ports; p += 2)
        {
            d.ConnectionA.Add(p);
            d.ConnectionB.Add(p + 1);
            d.ConnectionKinds.Add((byte)EdgeKind.Patch);
            d.ConnectionLifecycles.Add((byte)Lifecycle.InService);
        }
        Rows(d, circuits, circuits.Keys);
        return d;
    }

    private static void Rows(GraphData d, SortedDictionary<long, Circuit> circuits, IEnumerable<long> ids)
    {
        foreach (var id in ids.Order())
        {
            if (!circuits.TryGetValue(id, out var c))
            {
                continue;
            }
            d.CircuitIds.Add(id);
            d.CircuitLayers.Add((byte)c.Layer);
            foreach (var hop in c.Hops)
            {
                d.HopCircuits.Add(id);
                d.HopTerminals.Add(hop);
            }
            foreach (var carrier in c.Carriers)
            {
                d.DependencyCircuits.Add(id);
                d.DependencyCarriers.Add(carrier);
            }
            foreach (var service in c.Services)
            {
                d.ServiceCircuitServices.Add(service);
                d.ServiceCircuitCircuits.Add(id);
            }
        }
    }

    private static GraphChangeBatch Batch(string watermark, SortedDictionary<long, Circuit> circuits, HashSet<long> changed)
    {
        var keys = new GraphKeys();
        keys.Circuits.UnionWith(changed);
        var rows = new GraphData();
        Rows(rows, circuits, changed);
        return new GraphChangeBatch(watermark, false, keys, rows, changed.Count);
    }

    private static Circuit RandomCircuit(Random random, long id, SortedDictionary<long, Circuit> circuits)
    {
        var start = random.Next(1, Ports - 4);
        var hops = Enumerable.Range(start, random.Next(1, 5)).Select(p => (long)p).ToArray();
        // Carriers have lower ids, so the layers never form a cycle.
        var carriers = circuits.Keys.Where(c => c < id && random.Next(4) == 0).Take(2).ToArray();
        var services = Enumerable.Range(0, random.Next(0, 3)).Select(_ => (long)random.Next(900, 910)).Distinct().ToArray();
        return new Circuit((CircuitLayer)random.Next(3), hops, carriers, services);
    }

    /// <summary>Adds, reroutes and removes a few circuits; returns the circuits whose rows changed.</summary>
    private static HashSet<long> Change(Random random, SortedDictionary<long, Circuit> circuits, ref long next)
    {
        var changed = new HashSet<long>();
        for (var step = 0; step < 3; step++)
        {
            switch (random.Next(3))
            {
                case 0:
                    var id = next++;
                    circuits[id] = RandomCircuit(random, id, circuits);
                    changed.Add(id);
                    break;
                case 1 when circuits.Count > 0:
                    var rerouted = circuits.Keys.ElementAt(random.Next(circuits.Count));
                    circuits[rerouted] = RandomCircuit(random, rerouted, circuits);
                    changed.Add(rerouted);
                    break;
                case 2 when circuits.Count > 0:
                    // As the database cascades: circuits riding on a removed one lose it as a carrier.
                    var removed = circuits.Keys.ElementAt(random.Next(circuits.Count));
                    circuits.Remove(removed);
                    changed.Add(removed);
                    foreach (var (other, c) in circuits.Where(c => c.Value.Carriers.Contains(removed)).ToList())
                    {
                        circuits[other] = c with { Carriers = [.. c.Carriers.Where(x => x != removed)] };
                        changed.Add(other);
                    }
                    break;
            }
        }
        return changed;
    }

    /// <summary>Everything traversals read about circuits and services, by external id.</summary>
    private static string Circuits(Cmdb.Graph.Graph g, IEnumerable<long> circuitIds, IEnumerable<long> serviceIds)
    {
        var lines = new List<string>();
        foreach (var id in circuitIds.Order())
        {
            if (!g.TryGetCircuit(id, out var c))
            {
                lines.Add($"circuit {id}: none");
                continue;
            }
            lines.Add($"circuit {id} {g.LayerOf(c)}: hops {Ids(g.HopsOf(c), g.TerminalId, sort: false)}"
                + $" carriers {Ids(g.CarriersOf(c), g.CircuitId)} dependents {Ids(g.DependentsOf(c), g.CircuitId)}"
                + $" services {Ids(g.ServicesOf(c), g.ServiceId)}");
        }
        foreach (var id in serviceIds.Order())
        {
            lines.Add(g.TryGetService(id, out var s) ? $"service {id}: {Ids(g.CircuitsOf(s), g.CircuitId)}" : $"service {id}: none");
        }
        for (var p = 1L; p <= Ports; p++)
        {
            g.TryGetNode(p, out var node);
            lines.Add($"port {p}: {Ids(g.CircuitsThrough(node), g.CircuitId)}");
        }
        return string.Join('\n', lines);
    }

    private static string Ids(ReadOnlySpan<int> indexes, Func<int, long> id, bool sort = true)
    {
        var ids = indexes.ToArray().Select(id);
        return string.Join(',', sort ? ids.Order() : ids);
    }

    private static byte[] Bytes(Cmdb.Graph.Graph g)
    {
        using var stream = new MemoryStream();
        GraphSnapshot.Write(g, stream);
        return stream.ToArray();
    }

    [Fact]
    public void Random_circuit_changes_as_deltas_give_the_graph_a_rebuild_gives()
    {
        var random = new Random(121);
        var circuits = new SortedDictionary<long, Circuit>();
        var next = 1000L;
        for (var i = 0; i < 40; i++)
        {
            var id = next++;
            circuits[id] = RandomCircuit(random, id, circuits);
        }
        var production = GraphBuilder.Build(Network(circuits), "0");
        var graph = production;
        var batches = new List<GraphChangeBatch>();
        var seen = new HashSet<long>(circuits.Keys);
        var services = Enumerable.Range(900, 10).Select(s => (long)s).ToArray();

        for (var round = 1; round <= 60; round++)
        {
            var changed = Change(random, circuits, ref next);
            seen.UnionWith(changed);
            var batch = Batch(round.ToString(System.Globalization.CultureInfo.InvariantCulture), circuits, changed);
            batches.Add(batch);

            graph = GraphChanges.TryDelta(graph, batch).ShouldNotBeNull($"round {round}");

            var rebuilt = GraphBuilder.Build(Network(circuits), batch.Watermark);
            Circuits(graph, seen, services).ShouldBe(Circuits(rebuilt, seen, services), $"round {round}");
            graph.CircuitCount.ShouldBeGreaterThanOrEqualTo(rebuilt.CircuitCount);
        }

        var built = GraphBuilder.Build(Network(circuits), "60");
        Bytes(GraphChanges.Compact(production, batches)).ShouldBe(Bytes(built));
        var flat = GraphChanges.Flatten(graph, batches);
        flat.IsOverlay.ShouldBeFalse();
        Bytes(flat).ShouldBe(Bytes(built));
    }

    [Fact]
    public void A_circuit_on_a_carrier_that_does_not_exist_or_a_removed_carrier_still_in_use_rebuild()
    {
        var circuits = new SortedDictionary<long, Circuit>
        {
            [1000] = new(CircuitLayer.Physical, [1, 2], [], []),
            [1001] = new(CircuitLayer.Transmission, [1, 2], [1000], [900]),
        };
        var production = GraphBuilder.Build(Network(circuits), "0");

        var unknown = new SortedDictionary<long, Circuit>(circuits) { [1002] = new(CircuitLayer.Logical, [], [4242], []) };
        GraphChanges.TryDelta(production, Batch("1", unknown, [1002])).ShouldBeNull();

        // Circuit 1000 is removed but 1001, which rides on it, is not in the batch.
        var removed = new SortedDictionary<long, Circuit>(circuits);
        removed.Remove(1000);
        GraphChanges.TryDelta(production, Batch("2", removed, [1000])).ShouldBeNull();

        // A hop on a terminal that is not in the graph.
        var stray = new SortedDictionary<long, Circuit>(circuits) { [1003] = new(CircuitLayer.Physical, [1, 99999], [], []) };
        GraphChanges.TryDelta(production, Batch("3", stray, [1003])).ShouldBeNull();
    }
}

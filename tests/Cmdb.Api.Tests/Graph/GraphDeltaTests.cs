using Cmdb.Graph;

namespace Cmdb.Api.Tests.Graph;

/// <summary>
/// Change-stream batches as a delta on the graph (#81): the same graph as rebuilding from rows, in time that follows the
/// batch, and a rebuild whenever the batch changes structure.
/// </summary>
public sealed class GraphDeltaTests
{
    private static readonly EdgeKind[] Kinds = [EdgeKind.Patch, EdgeKind.Splice, EdgeKind.Termination];

    /// <summary>Ports 1..Ports on ten pieces of equipment, and cables whose conductor ends are 10001, 10002, ….</summary>
    private const int Ports = 200;

    private static GraphData Network(IEnumerable<(long A, long B, EdgeKind Kind)> connections)
    {
        var d = new GraphData();
        for (var e = 0; e < 10; e++)
        {
            d.EquipmentIds.Add(100 + e);
            d.EquipmentSites.Add(1 + (e % 3));
        }
        for (var p = 1; p <= Ports; p++)
        {
            d.PortTerminals.Add(p);
            d.PortEquipment.Add(100 + ((p - 1) % 10));
        }
        for (var c = 0; c < 20; c++)
        {
            d.CableIds.Add(500 + c);
            d.CableLifecycles.Add((byte)Lifecycle.InService);
            d.ConductorIds.Add(600 + c);
            d.ConductorCables.Add(500 + c);
            d.EndTerminals.AddRange([10001 + (2 * c), 10002 + (2 * c)]);
            d.EndConductors.AddRange([600 + c, 600 + c]);
        }
        foreach (var (a, b, kind) in connections)
        {
            d.ConnectionA.Add(a);
            d.ConnectionB.Add(b);
            d.ConnectionKinds.Add((byte)kind);
            d.ConnectionLifecycles.Add((byte)Lifecycle.InService);
        }
        return d;
    }

    private static long[] Terminals() => [.. Enumerable.Range(1, Ports).Select(p => (long)p), .. Enumerable.Range(10001, 40).Select(t => (long)t)];

    /// <summary>A terminal's connections as (other terminal, kind), sorted: what traversals see.</summary>
    private static string Adjacency(Cmdb.Graph.Graph g)
    {
        var lines = new List<string>();
        foreach (var id in Terminals())
        {
            g.TryGetNode(id, out var node).ShouldBeTrue();
            var targets = g.Neighbours(node);
            var kinds = g.NeighbourKinds(node);
            var edges = new List<string>();
            for (var i = 0; i < targets.Length; i++)
            {
                edges.Add($"{g.TerminalId(targets[i])}/{kinds[i]}");
            }
            edges.Sort(StringComparer.Ordinal);
            lines.Add($"{id}: {string.Join(' ', edges)}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>The batch the change feed would give: the terminals whose connections changed, and their rows now.</summary>
    private static GraphChangeBatch Batch(string watermark, HashSet<long> terminals, List<(long A, long B, EdgeKind Kind)> connections)
    {
        var keys = new GraphKeys();
        keys.Terminals.UnionWith(terminals);
        var rows = new GraphData();
        foreach (var (a, b, kind) in connections.Where(c => terminals.Contains(c.A) || terminals.Contains(c.B)))
        {
            rows.ConnectionA.Add(a);
            rows.ConnectionB.Add(b);
            rows.ConnectionKinds.Add((byte)kind);
            rows.ConnectionLifecycles.Add((byte)Lifecycle.InService);
        }
        return new GraphChangeBatch(watermark, false, keys, rows, terminals.Count);
    }

    /// <summary>Rewires a few connections, keeping one connection of each kind per terminal; returns the terminals touched.</summary>
    private static HashSet<long> Rewire(Random random, List<(long A, long B, EdgeKind Kind)> connections)
    {
        var touched = new HashSet<long>();
        var all = Terminals();
        for (var step = 0; step < 4; step++)
        {
            if (connections.Count > 0 && random.Next(3) == 0)
            {
                var i = random.Next(connections.Count);
                touched.UnionWith([connections[i].A, connections[i].B]);
                connections.RemoveAt(i);
                continue;
            }
            var a = all[random.Next(all.Length)];
            var b = all[random.Next(all.Length)];
            var kind = Kinds[random.Next(Kinds.Length)];
            if (a == b || connections.Any(c => (c.A == a && c.B == b) || (c.A == b && c.B == a)
                || ((c.A == a || c.B == a || c.A == b || c.B == b) && c.Kind == kind)))
            {
                continue;
            }
            connections.Add((a, b, kind));
            touched.UnionWith([a, b]);
        }
        return touched;
    }

    [Fact]
    public void Random_rewiring_as_deltas_gives_the_graph_a_rebuild_gives()
    {
        var random = new Random(81);
        var connections = new List<(long A, long B, EdgeKind Kind)>();
        for (var i = 0; i < 150; i++)
        {
            Rewire(random, connections);
        }
        var production = GraphBuilder.Build(Network(connections), "0");
        var graph = production;
        var before = Adjacency(production);
        var batches = new List<GraphChangeBatch>();

        for (var round = 1; round <= 40; round++)
        {
            var touched = Rewire(random, connections);
            var batch = Batch(round.ToString(System.Globalization.CultureInfo.InvariantCulture), touched, connections);
            batches.Add(batch);

            graph = GraphChanges.TryDelta(graph, batch).ShouldNotBeNull($"round {round}");

            var rebuilt = GraphBuilder.Build(Network(connections), batch.Watermark);
            Adjacency(graph).ShouldBe(Adjacency(rebuilt), $"round {round}");
            graph.Version.ShouldBe(batch.Watermark);
        }

        // Rebuilding the base's rows with every batch gives the same graph again, without delta, and so does folding the
        // delta into new adjacency arrays: byte for byte.
        var compacted = GraphChanges.Compact(production, batches);
        compacted.IsOverlay.ShouldBeFalse();
        compacted.Version.ShouldBe("40");
        Adjacency(compacted).ShouldBe(Adjacency(graph));
        var flat = GraphChanges.Flatten(graph, batches);
        flat.IsOverlay.ShouldBeFalse();
        Bytes(flat).ShouldBe(Bytes(compacted));
        // The delta shares the base's arrays and leaves production as it was.
        Adjacency(production).ShouldBe(before);
    }

    [Fact]
    public void Lifecycles_the_delta_does_not_hold_survive_folding_it_in()
    {
        var data = Network([(1, 2, EdgeKind.Patch), (2, 10001, EdgeKind.Splice)]);
        var production = GraphBuilder.Build(data, "1");

        // The patch goes into service planned, and cable 500 (conductor ends 10001, 10002) is being decommissioned.
        var keys = new GraphKeys();
        keys.Terminals.UnionWith([1, 2]);
        keys.Cables.Add(500);
        var rows = new GraphData();
        rows.ConnectionA.AddRange([1, 2]);
        rows.ConnectionB.AddRange([2, 10001]);
        rows.ConnectionKinds.AddRange([(byte)EdgeKind.Patch, (byte)EdgeKind.Splice]);
        rows.ConnectionLifecycles.AddRange([(byte)Lifecycle.Planned, (byte)Lifecycle.InService]);
        rows.CableIds.Add(500);
        rows.CableLifecycles.Add((byte)Lifecycle.Decommissioning);
        rows.ConductorIds.Add(600);
        rows.ConductorCables.Add(500);
        rows.EndTerminals.AddRange([10001, 10002]);
        rows.EndConductors.AddRange([600, 600]);
        var batch = new GraphChangeBatch("2", false, keys, rows, 3);

        var delta = GraphChanges.TryDelta(production, batch).ShouldNotBeNull();
        delta.OverlayNodes.ShouldBe(0);

        var flat = GraphChanges.Flatten(delta, [batch]);
        Bytes(flat).ShouldBe(Bytes(GraphChanges.Compact(production, [batch])));
        Bytes(flat).ShouldNotBe(Bytes(production));
    }

    private static byte[] Bytes(Cmdb.Graph.Graph g)
    {
        using var stream = new MemoryStream();
        GraphSnapshot.Write(g, stream);
        return stream.ToArray();
    }

    [Fact]
    public void A_delta_is_production_plans_are_made_on_top_of_it_and_it_cannot_be_written_as_is()
    {
        List<(long, long, EdgeKind)> connections = [(1, 2, EdgeKind.Patch)];
        var production = GraphBuilder.Build(Network(connections), "1");
        connections.Add((2, 10001, EdgeKind.Splice));

        var delta = GraphChanges.TryDelta(production, Batch("2", [2, 10001], connections)).ShouldNotBeNull();

        delta.IsOverlay.ShouldBeTrue();
        delta.Base.ShouldBeSameAs(delta);
        delta.OverlayNodes.ShouldBe(2);
        var (plan, issues) = delta.WithChanges([new GraphEdgeChange(10002, 3, EdgeKind.Splice, Add: true)]);
        issues.ShouldBeEmpty();
        plan.Base.ShouldBeSameAs(delta);
        plan.TryGetNode(1, out var start);
        GraphTrace.Physical(plan, start).Nodes.Select(plan.TerminalId).ShouldBe([1, 2, 10001, 10002, 3]);

        Should.Throw<ArgumentException>(() => GraphSnapshot.Write(delta, new MemoryStream()));
        Should.Throw<ArgumentException>(() => GraphData.From(delta));
    }

    [Fact]
    public void Objects_whose_structure_is_unchanged_stay_a_delta_and_rows_that_do_not_fit_rebuild()
    {
        List<(long, long, EdgeKind)> connections = [(1, 2, EdgeKind.Patch)];
        var data = Network(connections);
        var production = GraphBuilder.Build(data, "1");

        // A renamed piece of equipment and a cable with a new lifecycle: same site, ports, conductors and ends.
        var same = new GraphKeys();
        same.Equipment.Add(100);
        same.Cables.Add(500);
        var rows = new GraphData();
        rows.EquipmentIds.Add(100);
        rows.EquipmentSites.Add(1);
        rows.PortTerminals.AddRange(data.PortTerminals.Where((_, i) => data.PortEquipment[i] == 100));
        rows.PortEquipment.AddRange(rows.PortTerminals.Select(_ => 100L));
        rows.CableIds.Add(500);
        rows.CableLifecycles.Add((byte)Lifecycle.Decommissioning);
        rows.ConductorIds.Add(600);
        rows.ConductorCables.Add(500);
        rows.EndTerminals.AddRange([10001, 10002]);
        rows.EndConductors.AddRange([600, 600]);
        var delta = GraphChanges.TryDelta(production, new GraphChangeBatch("2", false, same, rows, 2)).ShouldNotBeNull();
        delta.Version.ShouldBe("2");
        delta.OverlayNodes.ShouldBe(0);

        // Moved and removed objects are a delta too (#123, GraphStructureDeltaTests). A connection to a terminal that is neither in the graph nor new in the batch waits for the rebuild.
        connections.Add((3, 77777, EdgeKind.Patch));
        GraphChanges.TryDelta(production, Batch("7", [3, 77777], connections)).ShouldBeNull();

        // A new cable whose conductor does not have two ends.
        var odd = new GraphKeys();
        odd.Cables.Add(900);
        var oddRows = new GraphData();
        oddRows.CableIds.Add(900);
        oddRows.CableLifecycles.Add((byte)Lifecycle.Planned);
        oddRows.ConductorIds.Add(950);
        oddRows.ConductorCables.Add(900);
        oddRows.EndTerminals.Add(20005);
        oddRows.EndConductors.Add(950);
        GraphChanges.TryDelta(production, new GraphChangeBatch("8", false, odd, oddRows, 1)).ShouldBeNull();
    }

    /// <summary>
    /// A batch as the feed reads it when equipment 200 is installed at site 0 (older than every site in the graph) with
    /// <paramref name="ports"/>, cable 900 is pulled with conductor 950 between <paramref name="ends"/>, and the first
    /// new port is patched to port 5 and the second spliced to the cable, and circuit 7000 runs from the second port
    /// through the cable, carrying service 800 (#121).
    /// </summary>
    private static (GraphChangeBatch Batch, GraphData Rows) Installation(long[] ports, long[] ends)
    {
        var keys = new GraphKeys();
        keys.Equipment.Add(200);
        keys.Cables.Add(900);
        keys.Terminals.UnionWith([ports[0], 5, ports[1], ends[0]]);
        var rows = new GraphData();
        rows.EquipmentIds.Add(200);
        rows.EquipmentSites.Add(0);
        rows.PortTerminals.AddRange(ports);
        rows.PortEquipment.AddRange(ports.Select(_ => 200L));
        rows.CableIds.Add(900);
        rows.CableLifecycles.Add((byte)Lifecycle.Planned);
        rows.ConductorIds.Add(950);
        rows.ConductorCables.Add(900);
        rows.EndTerminals.AddRange(ends);
        rows.EndConductors.AddRange([950, 950]);
        rows.ConnectionA.AddRange([ports[0], ports[1]]);
        rows.ConnectionB.AddRange([5, ends[0]]);
        rows.ConnectionKinds.AddRange([(byte)EdgeKind.Patch, (byte)EdgeKind.Splice]);
        rows.ConnectionLifecycles.AddRange([(byte)Lifecycle.Planned, (byte)Lifecycle.Planned]);
        keys.Circuits.Add(7000);
        rows.CircuitIds.Add(7000);
        rows.CircuitLayers.Add((byte)CircuitLayer.Physical);
        rows.HopCircuits.AddRange([7000, 7000, 7000]);
        rows.HopTerminals.AddRange([ports[1], ends[0], ends[1]]);
        rows.ServiceCircuitServices.Add(800);
        rows.ServiceCircuitCircuits.Add(7000);
        return (new GraphChangeBatch("2", false, keys, rows, 7), rows);
    }

    private static GraphData With(GraphData data, GraphData rows)
    {
        data.EquipmentIds.AddRange(rows.EquipmentIds);
        data.EquipmentSites.AddRange(rows.EquipmentSites);
        data.PortTerminals.AddRange(rows.PortTerminals);
        data.PortEquipment.AddRange(rows.PortEquipment);
        data.CableIds.AddRange(rows.CableIds);
        data.CableLifecycles.AddRange(rows.CableLifecycles);
        data.ConductorIds.AddRange(rows.ConductorIds);
        data.ConductorCables.AddRange(rows.ConductorCables);
        data.EndTerminals.AddRange(rows.EndTerminals);
        data.EndConductors.AddRange(rows.EndConductors);
        data.ConnectionA.AddRange(rows.ConnectionA);
        data.ConnectionB.AddRange(rows.ConnectionB);
        data.ConnectionKinds.AddRange(rows.ConnectionKinds);
        data.ConnectionLifecycles.AddRange(rows.ConnectionLifecycles);
        data.CircuitIds.AddRange(rows.CircuitIds);
        data.CircuitLayers.AddRange(rows.CircuitLayers);
        data.HopCircuits.AddRange(rows.HopCircuits);
        data.HopTerminals.AddRange(rows.HopTerminals);
        data.ServiceCircuitServices.AddRange(rows.ServiceCircuitServices);
        data.ServiceCircuitCircuits.AddRange(rows.ServiceCircuitCircuits);
        return data;
    }

    [Fact]
    public void New_equipment_and_cables_join_the_delta_and_fold_in_as_a_rebuild_would_build_them()
    {
        List<(long, long, EdgeKind)> connections = [(1, 2, EdgeKind.Patch)];
        var production = GraphBuilder.Build(Network(connections), "1");
        // Ports out of order, as rows may come: the arrays get them sorted.
        var (batch, rows) = Installation([20004, 20001, 20003, 20002], [20005, 20006]);
        var built = GraphBuilder.Build(With(Network(connections), rows), "2");

        var delta = GraphChanges.TryDelta(production, batch).ShouldNotBeNull();

        delta.TryGetEquipment(200, out var equipment).ShouldBeTrue();
        delta.SiteId(delta.SiteIndexOfEquipment(equipment)).ShouldBe(0);
        delta.TryGetNode(20004, out var port).ShouldBeTrue();
        GraphTrace.Physical(delta, port).Nodes.Select(delta.TerminalId).ShouldBe([20004, 5]);
        delta.TryGetNode(20001, out port);
        GraphTrace.Physical(delta, port).Nodes.Select(delta.TerminalId).ShouldBe([20001, 20005, 20006]);
        delta.TryGetCable(900, out _).ShouldBeTrue();
        delta.CableCount.ShouldBe(production.CableCount + 1);
        delta.TryGetCircuit(7000, out var circuit).ShouldBeTrue();
        delta.HopsOf(circuit).ToArray().Select(delta.TerminalId).ShouldBe([20001, 20005, 20006]);
        delta.SiteCount.ShouldBe(production.SiteCount + 1);

        // A plan's planned objects come after the delta's new ones.
        var (plan, issues) = delta.WithChanges([new GraphNewEquipment(-1, 0, [-10001])]);
        issues.ShouldBeEmpty();
        plan.TryGetEquipment(-1, out var planned).ShouldBeTrue();
        planned.ShouldBeGreaterThan(equipment);
        plan.EquipmentAt(plan.SiteIndexOfEquipment(planned)).ToArray().ShouldBe([equipment, planned]);

        var flat = GraphChanges.Flatten(delta, [batch]);
        flat.IsOverlay.ShouldBeFalse();
        Bytes(flat).ShouldBe(Bytes(built));
        Bytes(GraphChanges.Compact(production, [batch])).ShouldBe(Bytes(built));
        flat.TryGetEquipment(200, out equipment).ShouldBeTrue();
        flat.PortsOf(equipment).ToArray().Select(flat.TerminalId).ShouldBe([20001, 20002, 20003, 20004]);
        flat.EquipmentAt(flat.SiteIndexOfEquipment(equipment)).ToArray().ShouldBe([equipment]);
        flat.TryGetCable(900, out var cable).ShouldBeTrue();
        flat.EndsOf(cable).ToArray().Select(flat.TerminalId).ShouldBe([20005, 20006]);
        flat.TryGetNode(20005, out var end).ShouldBeTrue();
        flat.CircuitsThrough(end).ToArray().Select(flat.CircuitId).ShouldBe([7000]);
        flat.TryGetService(800, out var service).ShouldBeTrue();
        flat.CircuitsOf(service).ToArray().Select(flat.CircuitId).ShouldBe([7000]);
    }

    [Fact]
    public void A_cable_the_delta_added_can_change_again_in_the_next_batch()
    {
        // #163: the next batch names the delta's own new cable again. Its conductors sit after the base's arrays, and
        // reading them as base conductors threw and stopped the host.
        List<(long, long, EdgeKind)> connections = [(1, 2, EdgeKind.Patch)];
        var production = GraphBuilder.Build(Network(connections), "1");
        var (batch, _) = Installation([20004, 20001, 20003, 20002], [20005, 20006]);
        var delta = GraphChanges.TryDelta(production, batch).ShouldNotBeNull();

        var again = GraphChanges.TryDelta(delta, batch).ShouldNotBeNull();
        again.TryGetCable(900, out var cable).ShouldBeTrue();
        again.EndsOf(cable).ToArray().Select(again.TerminalId).Order().ShouldBe([20005, 20006]);
    }

    [Fact]
    public void New_ids_below_the_arrays_own_are_sorted_in_when_the_delta_is_folded_in()
    {
        List<(long, long, EdgeKind)> connections = [(1, 2, EdgeKind.Patch)];
        var production = GraphBuilder.Build(Network(connections), "1");
        // Terminal ids below the conductor ends 10001 and up cannot simply follow the arrays.
        var (batch, rows) = Installation([5001, 5002], [5003, 5004]);

        var delta = GraphChanges.TryDelta(production, batch).ShouldNotBeNull();
        delta.TryGetNode(5001, out _).ShouldBeTrue();

        // #123: the arrays are renumbered rather than appended to.
        var built = Bytes(GraphBuilder.Build(With(Network(connections), rows), "2"));
        Bytes(GraphChanges.Flatten(delta, [batch])).ShouldBe(built);
        Bytes(GraphChanges.Compact(production, [batch])).ShouldBe(built);
    }
}

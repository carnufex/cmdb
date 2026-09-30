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

        Bytes(GraphChanges.Flatten(delta, [batch])).ShouldBe(Bytes(GraphChanges.Compact(production, [batch])));
        Bytes(GraphChanges.Flatten(delta, [batch])).ShouldNotBe(Bytes(production));
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
    public void Objects_whose_structure_is_unchanged_stay_a_delta_and_new_or_moved_ones_rebuild()
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

        // Equipment moved to another site.
        var moved = new GraphData();
        moved.EquipmentIds.Add(100);
        moved.EquipmentSites.Add(3);
        moved.PortTerminals.AddRange(rows.PortTerminals);
        moved.PortEquipment.AddRange(rows.PortEquipment);
        var movedKeys = new GraphKeys();
        movedKeys.Equipment.Add(100);
        GraphChanges.TryDelta(production, new GraphChangeBatch("3", false, movedKeys, moved, 1)).ShouldBeNull();

        // New equipment, a removed cable and a changed circuit are structure too.
        var added = new GraphKeys();
        added.Equipment.Add(999);
        var addedRows = new GraphData();
        addedRows.EquipmentIds.Add(999);
        addedRows.EquipmentSites.Add(1);
        GraphChanges.TryDelta(production, new GraphChangeBatch("4", false, added, addedRows, 1)).ShouldBeNull();
        var removed = new GraphKeys();
        removed.Cables.Add(501);
        GraphChanges.TryDelta(production, new GraphChangeBatch("5", false, removed, new GraphData(), 1)).ShouldBeNull();
        var circuit = new GraphKeys();
        circuit.Circuits.Add(1);
        GraphChanges.TryDelta(production, new GraphChangeBatch("6", false, circuit, new GraphData(), 1)).ShouldBeNull();

        // A connection to a terminal the graph does not have yet waits for the rebuild.
        connections.Add((3, 77777, EdgeKind.Patch));
        GraphChanges.TryDelta(production, Batch("7", [3, 77777], connections)).ShouldBeNull();
    }
}

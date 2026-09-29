using Cmdb.Graph;

namespace Cmdb.Api.Tests.Graph;

public sealed class GraphBuilderTests
{
    // Two sites, a switch port on each, and one fibre between them:
    // p1 (site 1) —splice— t10 ═conductor═ t11 —patch— p2 (site 2).
    // Circuit 100 is the fibre path, circuit 200 rides on it and carries service 900.
    private static GraphData Sample(bool reversed = false)
    {
        var d = new GraphData();
        void Add<T>(List<T> list, params T[] items) => list.AddRange(reversed ? items.Reverse() : items);
        Add(d.EquipmentIds, 7L, 8L);
        Add(d.EquipmentSites, 1L, 2L);
        Add(d.PortTerminals, 1L, 2L);
        Add(d.PortEquipment, 7L, 8L);
        d.CableIds.Add(50);
        d.CableLifecycles.Add((byte)Lifecycle.InService);
        d.ConductorIds.Add(60);
        d.ConductorCables.Add(50);
        Add(d.EndTerminals, 10L, 11L);
        Add(d.EndConductors, 60L, 60L);
        Add(d.ConnectionA, 1L, 11L);
        Add(d.ConnectionB, 10L, 2L);
        Add(d.ConnectionKinds, (byte)EdgeKind.Splice, (byte)EdgeKind.Patch);
        Add(d.ConnectionLifecycles, (byte)Lifecycle.InService, (byte)Lifecycle.Planned);
        Add(d.CircuitIds, 100L, 200L);
        Add(d.CircuitLayers, (byte)CircuitLayer.Physical, (byte)CircuitLayer.Logical);
        d.HopCircuits.AddRange([100, 100, 100, 100, 200, 200]);
        d.HopTerminals.AddRange([1, 10, 11, 2, 1, 2]);
        d.DependencyCircuits.Add(200);
        d.DependencyCarriers.Add(100);
        d.ServiceCircuitServices.Add(900);
        d.ServiceCircuitCircuits.Add(200);
        return d;
    }

    [Fact]
    public void Terminals_become_dense_nodes_joined_by_connections_and_conductors()
    {
        var g = GraphBuilder.Build(Sample(), "v1");

        g.NodeCount.ShouldBe(4);
        g.EdgeCount.ShouldBe(3);
        g.TryGetNode(10, out var t10).ShouldBeTrue();
        g.TryGetNode(999, out _).ShouldBeFalse();
        g.KindOf(t10).ShouldBe(TerminalKind.ConductorEnd);
        g.CableOf(t10).ShouldBe(50);
        Ids(g, g.Neighbours(t10)).ShouldBe([1, 11]);
        g.NeighbourKinds(t10).ToArray().ShouldBe([EdgeKind.Splice, EdgeKind.Conductor]);

        g.TryGetNode(2, out var p2).ShouldBeTrue();
        g.EquipmentOf(p2).ShouldBe(8);
        g.SiteOf(p2).ShouldBe(2);
        g.EdgeLifecycles[g.EdgeStart[p2]].ShouldBe(Lifecycle.Planned);
    }

    [Fact]
    public void Circuits_know_their_hops_the_circuits_on_top_and_the_services()
    {
        var g = GraphBuilder.Build(Sample(), "v1");
        g.TryGetNode(11, out var t11).ShouldBeTrue();

        var through = g.NodeCircuits.AsSpan(g.NodeCircuitStart[t11], g.NodeCircuitStart[t11 + 1] - g.NodeCircuitStart[t11]).ToArray();
        through.Select(c => g.CircuitIds[c]).ShouldBe([100L]);
        var fibre = through[0];
        g.HopNodes[g.HopStart[fibre]..g.HopStart[fibre + 1]].Select(g.TerminalId).ShouldBe([1L, 10L, 11L, 2L]);
        var riding = g.Dependents[g.DependentStart[fibre]..g.DependentStart[fibre + 1]];
        riding.Select(c => g.CircuitIds[c]).ShouldBe([200L]);
        g.CircuitServices[g.CircuitServiceStart[riding[0]]..g.CircuitServiceStart[riding[0] + 1]].Select(s => g.ServiceIds[s]).ShouldBe([900L]);
    }

    [Fact]
    public void The_same_data_in_any_order_gives_identical_arrays()
    {
        var a = GraphBuilder.Build(Sample(), "v1");
        var b = GraphBuilder.Build(Sample(reversed: true), "v1");

        Snapshot(a).ShouldBe(Snapshot(b));
    }

    [Fact]
    public void Snapshots_round_trip_and_refuse_another_data_version()
    {
        var graph = GraphBuilder.Build(Sample(), "v1");
        var bytes = Snapshot(graph);

        var back = GraphSnapshot.Read(new MemoryStream(bytes), "v1").ShouldNotBeNull();
        var stale = GraphSnapshot.Read(new MemoryStream(bytes), "v2");

        Snapshot(back).ShouldBe(bytes);
        stale.ShouldBeNull();
        GraphSnapshot.Read(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8])).ShouldBeNull();
    }

    [Fact]
    public void Inconsistent_data_is_refused()
    {
        var data = Sample();
        data.ConnectionB[0] = 12345;

        Should.Throw<InvalidOperationException>(() => GraphBuilder.Build(data, "v1")).Message.ShouldContain("12345");
    }

    private static long[] Ids(Cmdb.Graph.Graph g, ReadOnlySpan<int> nodes)
    {
        var ids = new long[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            ids[i] = g.TerminalId(nodes[i]);
        }
        return ids;
    }

    private static byte[] Snapshot(Cmdb.Graph.Graph g)
    {
        using var stream = new MemoryStream();
        GraphSnapshot.Write(g, stream);
        return stream.ToArray();
    }
}

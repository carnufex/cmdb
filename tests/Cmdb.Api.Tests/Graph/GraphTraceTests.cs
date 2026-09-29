using Cmdb.Graph;

namespace Cmdb.Api.Tests.Graph;

public sealed class GraphTraceTests
{
    // A radio port to a switch port across two cables with a cross-connect in between:
    // 1 —patch— 2(ODF) —splice— 10 ═ 11 —splice— 3(ODF) —patch— 4(ODF) —splice— 20 ═ 21 —splice— 5(ODF) —patch— 6
    private static GraphData Chain(EdgeKind crossConnect = EdgeKind.Patch, EdgeKind last = EdgeKind.Patch)
    {
        var d = new GraphData();
        d.EquipmentIds.AddRange([100, 101, 102]);
        d.EquipmentSites.AddRange([1, 2, 3]);
        d.PortTerminals.AddRange([1, 2, 3, 4, 5, 6]);
        d.PortEquipment.AddRange([100, 100, 101, 101, 102, 102]);
        d.CableIds.AddRange([50, 51]);
        d.CableLifecycles.AddRange([(byte)Lifecycle.InService, (byte)Lifecycle.InService]);
        d.ConductorIds.AddRange([60, 61]);
        d.ConductorCables.AddRange([50, 51]);
        d.EndTerminals.AddRange([10, 11, 20, 21]);
        d.EndConductors.AddRange([60, 60, 61, 61]);
        Connect(d, 1, 2, EdgeKind.Patch);
        Connect(d, 2, 10, EdgeKind.Splice);
        Connect(d, 3, 11, EdgeKind.Splice);
        Connect(d, 3, 4, crossConnect);
        Connect(d, 4, 20, EdgeKind.Splice);
        Connect(d, 5, 21, EdgeKind.Splice);
        Connect(d, 5, 6, last);
        return d;
    }

    private static void Connect(GraphData d, long a, long b, EdgeKind kind)
    {
        d.ConnectionA.Add(a);
        d.ConnectionB.Add(b);
        d.ConnectionKinds.Add((byte)kind);
        d.ConnectionLifecycles.Add((byte)Lifecycle.InService);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(11L)]
    [InlineData(6L)]
    public void Follows_patches_splices_and_conductors_end_to_end_from_anywhere_on_the_path(long start)
    {
        var g = GraphBuilder.Build(Chain(), "v");
        g.TryGetNode(start, out var node);

        var path = GraphTrace.Physical(g, node);

        var ids = path.Nodes.Select(g.TerminalId).ToArray();
        (ids.SequenceEqual([1L, 2, 10, 11, 3, 4, 20, 21, 5, 6]) || ids.SequenceEqual([6L, 5, 21, 20, 4, 3, 11, 10, 2, 1])).ShouldBeTrue(string.Join(",", ids));
        path.Complete.ShouldBeTrue();
        g.TerminalId(path.Nodes[path.StartIndex]).ShouldBe(start);
    }

    [Fact]
    public void Records_how_each_hop_is_reached()
    {
        var g = GraphBuilder.Build(Chain(), "v");
        g.TryGetNode(11, out var node);

        var path = GraphTrace.Physical(g, node);

        var forward = g.TerminalId(path.Nodes[0]) == 1;
        var edges = path.EdgesBefore.Skip(1).ToArray();
        var expected = new[] { EdgeKind.Patch, EdgeKind.Splice, EdgeKind.Conductor, EdgeKind.Splice, EdgeKind.Patch, EdgeKind.Splice, EdgeKind.Conductor, EdgeKind.Splice, EdgeKind.Patch };
        edges.ShouldBe(forward ? expected : [.. expected.Reverse()]);
    }

    [Fact]
    public void Follows_internal_connections_and_terminations()
    {
        var g = GraphBuilder.Build(Chain(EdgeKind.Internal, EdgeKind.Termination), "v");
        g.TryGetNode(1, out var node);

        var path = GraphTrace.Physical(g, node);

        path.Nodes.Select(g.TerminalId).ShouldBe([1L, 2, 10, 11, 3, 4, 20, 21, 5, 6]);
        path.EdgesBefore[5].ShouldBe(EdgeKind.Internal);
        path.EdgesBefore[9].ShouldBe(EdgeKind.Termination);
        path.Complete.ShouldBeTrue();
    }

    [Fact]
    public void Stops_at_a_branch_instead_of_guessing()
    {
        var data = Chain();
        data.PortTerminals.Add(7);
        data.PortEquipment.Add(101);
        Connect(data, 4, 7, EdgeKind.Patch); // a second patch on port 4
        var g = GraphBuilder.Build(data, "v");
        g.TryGetNode(1, out var node);

        var path = GraphTrace.Physical(g, node);

        path.Complete.ShouldBeFalse();
        path.StartIndex.ShouldBe(0); // from an end the path reads away from the start
        path.LastEnd.ShouldBe(TraceEnd.Branch);
        g.TerminalId(path.Nodes[^1]).ShouldBe(4);
    }

    [Fact]
    public void Detects_a_loop()
    {
        var data = Chain();
        Connect(data, 1, 6, EdgeKind.Patch); // closes the chain into a ring
        var g = GraphBuilder.Build(data, "v");
        g.TryGetNode(1, out var node);

        var path = GraphTrace.Physical(g, node);

        new[] { path.FirstEnd, path.LastEnd }.ShouldContain(TraceEnd.Loop);
        path.Nodes.Distinct().Count().ShouldBe(path.Nodes.Length);
    }

    [Fact]
    public void A_service_is_traced_down_through_its_layers()
    {
        var data = Chain();
        data.CircuitIds.AddRange([1, 2, 3]);
        data.CircuitLayers.AddRange([(byte)CircuitLayer.Logical, (byte)CircuitLayer.Transmission, (byte)CircuitLayer.Physical]);
        data.HopCircuits.AddRange([1, 1, 2, 2, 3, 3]);
        data.HopTerminals.AddRange([1, 6, 1, 6, 1, 6]);
        data.DependencyCircuits.AddRange([1, 2]);
        data.DependencyCarriers.AddRange([2, 3]);
        data.ServiceCircuitServices.Add(900);
        data.ServiceCircuitCircuits.Add(1);
        var g = GraphBuilder.Build(data, "v");
        g.TryGetService(900, out var service);

        var steps = GraphTrace.Service(g, service);

        steps.Select(s => (g.CircuitId(s.Circuit), s.Depth)).ShouldBe([(1L, 0), (2L, 1), (3L, 2)]);
        g.TryGetNode(11, out var middle);
        GraphTrace.ServicesThrough(g, middle).ShouldBeEmpty(); // circuits list only their end terminals here
        g.TryGetNode(6, out var end);
        GraphTrace.ServicesThrough(g, end).Select(g.ServiceId).ShouldBe([900L]);
    }
}

using Cmdb.Graph;

namespace Cmdb.Api.Tests.Graph;

public sealed class GraphImpactTests
{
    // Cable 50 (conductor 60, ends 10–11) carries physical circuits 1 and 2 between ports 1 and 2 on equipment 100/101.
    // Transmission 3 rides on 1; logical 4 rides on 3 and on 2; logical 5 rides on 3. Services: 900 on 4, 901 on 5,
    // 902 on 2 directly. Cable 51 carries nothing.
    private static Cmdb.Graph.Graph Network()
    {
        var d = new GraphData();
        d.EquipmentIds.AddRange([100, 101, 102]);
        d.EquipmentSites.AddRange([1, 2, 2]);
        d.PortTerminals.AddRange([1, 2, 3]);
        d.PortEquipment.AddRange([100, 101, 102]);
        d.CableIds.AddRange([50, 51]);
        d.CableLifecycles.AddRange([(byte)Lifecycle.InService, (byte)Lifecycle.InService]);
        d.ConductorIds.AddRange([60, 61]);
        d.ConductorCables.AddRange([50, 51]);
        d.EndTerminals.AddRange([10, 11, 20, 21]);
        d.EndConductors.AddRange([60, 60, 61, 61]);
        d.CircuitIds.AddRange([1, 2, 3, 4, 5]);
        d.CircuitLayers.AddRange([(byte)CircuitLayer.Physical, (byte)CircuitLayer.Physical, (byte)CircuitLayer.Transmission, (byte)CircuitLayer.Logical, (byte)CircuitLayer.Logical]);
        d.HopCircuits.AddRange([1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 5]);
        d.HopTerminals.AddRange([1, 10, 11, 2, 1, 10, 11, 2, 1, 2, 1, 2, 1, 2]);
        d.DependencyCircuits.AddRange([3, 4, 4, 5]);
        d.DependencyCarriers.AddRange([1, 3, 2, 3]);
        d.ServiceCircuitServices.AddRange([900, 901, 902]);
        d.ServiceCircuitCircuits.AddRange([4, 5, 2]);
        return GraphBuilder.Build(d, "v");
    }

    private static List<long> Path(Cmdb.Graph.Graph g, ImpactResult r, long service) =>
        [.. r.PathOf(Array.FindIndex(r.Services, s => g.ServiceId(s) == service)).Select(g.CircuitId)];

    [Fact]
    public void A_cable_cut_reaches_every_layer_and_service_through_the_shortest_path()
    {
        var g = Network();
        g.TryGetCable(50, out var cable);

        var r = GraphImpact.OfCable(g, cable);

        r.Direct.ShouldBe(2);
        r.Circuits.Select(g.CircuitId).ShouldBe([1L, 2, 3, 4, 5]);
        r.Services.Select(g.ServiceId).ShouldBe([900L, 901, 902]);
        Path(g, r, 902).ShouldBe([2L]);
        Path(g, r, 901).ShouldBe([5L, 3, 1]);
        Path(g, r, 900).ShouldBe([4L, 2]); // 4 rides on 2 directly: shorter than 4 → 3 → 1
    }

    [Fact]
    public void Equipment_and_sites_are_hit_through_their_ports()
    {
        var g = Network();
        g.TryGetEquipment(102, out var idle);
        g.TryGetSite(2, out var site);

        GraphImpact.OfEquipment(g, idle).Circuits.ShouldBeEmpty();
        var r = GraphImpact.OfSite(g, site);
        r.Direct.ShouldBe(5); // port 2 is an end of every circuit
        r.Services.Length.ShouldBe(3);
        r.PathOf(0).Count.ShouldBe(1);
    }

    [Fact]
    public void A_cable_without_circuits_affects_nothing()
    {
        var g = Network();
        g.TryGetCable(51, out var cable);

        var r = GraphImpact.OfCable(g, cable);

        r.Circuits.ShouldBeEmpty();
        r.Services.ShouldBeEmpty();
    }

    [Fact]
    public void Ownership_indexes_survive_a_snapshot_round_trip()
    {
        var g = Network();
        using var stream = new MemoryStream();
        GraphSnapshot.Write(g, stream);
        stream.Position = 0;

        var read = GraphSnapshot.Read(stream)!;

        read.TryGetSite(2, out var site).ShouldBeTrue();
        read.EquipmentAt(site).Length.ShouldBe(2);
        read.TryGetCable(50, out var cable).ShouldBeTrue();
        read.EndsOf(cable).ToArray().Select(read.TerminalId).ShouldBe([10L, 11]);
    }
}

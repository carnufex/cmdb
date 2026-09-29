using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Trace;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;

namespace Cmdb.Api.IntegrationTests.Graph;

/// <summary>Tracing (#9) against the small generated network (see <see cref="NetworkFixture"/>).</summary>
public sealed class TraceTests(ApiFactory factory)
{
    /// <summary>The network <see cref="NetworkFixture"/> loads: generation is deterministic.</summary>
    private static readonly Network Network = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Embedded);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_physical_circuit_is_the_physical_trace_from_its_first_terminal()
    {
        var network = Network;
        var g = GraphBuilder.Build(NetworkGraph.From(network), "v");
        var hops = network.Hops.GroupBy(h => h.CircuitId).ToDictionary(x => x.Key, x => x.OrderBy(h => h.Seq).Select(h => h.TerminalId).ToArray());
        var physical = network.Circuits.Where(c => c.Layer == "physical").ToList();

        physical.Count.ShouldBeGreaterThan(100);
        foreach (var circuit in physical)
        {
            g.TryGetNode(hops[circuit.Id][0], out var start).ShouldBeTrue();
            var path = GraphTrace.Physical(g, start);
            var ids = path.Nodes.Select(g.TerminalId).ToArray();
            if (ids[0] != hops[circuit.Id][0])
            {
                Array.Reverse(ids);
            }
            ids.ShouldBe(hops[circuit.Id], $"circuit {circuit.Code}");
            path.Complete.ShouldBeTrue(circuit.Code);
        }
    }

    [Fact]
    public async Task Traces_a_service_down_to_the_fibres_and_names_every_hop()
    {
        var (_, api) = await NetworkFixture.GetAsync(factory);
        var backhaul = Network.Services.First(s => s.Type == "mobile-backhaul");
        using var client = NetworkFixture.Client(api);

        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?service={backhaul.Id}", Ct))!;

        trace.Service!.Code.ShouldBe(backhaul.Code);
        trace.Circuits.Select(c => c.Layer).ShouldBe(["logical", "physical", "physical"], ignoreOrder: true);
        trace.Circuits.Single(c => c.Layer == "logical").Depth.ShouldBe(0);
        trace.Circuits.Where(c => c.Layer == "physical").ShouldAllBe(c => c.Depth == 1);
        trace.Circuits.SelectMany(c => c.Hops).ShouldAllBe(h => h.Kind != "unknown" && h.Site != null);
        trace.Circuits.Where(c => c.Layer == "physical").SelectMany(c => c.Hops.Skip(1)).ShouldAllBe(h => h.Edge != null);
        trace.Route.ShouldBeNull();
        trace.Sites.ShouldContain(s => s.Code.StartsWith("RAD-", StringComparison.Ordinal));
        trace.Sites.ShouldContain(s => s.Code.StartsWith("AGG-", StringComparison.Ordinal));
        trace.Cables.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Traces_the_physical_route_from_a_port_and_the_services_through_it()
    {
        var (_, api) = await NetworkFixture.GetAsync(factory);
        var circuit = Network.Circuits.First(c => c.Layer == "physical" && c.Lifecycle == "in_service");
        using var client = NetworkFixture.Client(api);

        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={circuit.A}", Ct))!;

        trace.Physical!.Complete.ShouldBeTrue();
        trace.Physical.Hops[0].TerminalId.ShouldBe(circuit.A);
        trace.Physical.Hops[^1].TerminalId.ShouldBe(circuit.B);
        trace.Physical.Hops.Skip(1).ShouldAllBe(h => h.Edge != null);
        trace.Physical.Hops.ShouldContain(h => h.Edge == "conductor");
        trace.Services.ShouldNotBeEmpty();
        trace.ElapsedMs.ShouldBeLessThan(50);
    }

    [Fact]
    public async Task Geometry_draws_the_route_on_the_map()
    {
        var (_, api) = await NetworkFixture.GetAsync(factory);
        var backhaul = Network.Services.First(s => s.Type == "mobile-backhaul");
        using var client = NetworkFixture.Client(api);

        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?service={backhaul.Id}&geometry=true", Ct))!;

        var route = trace.Route.ShouldNotBeNull();
        route.Sites.Select(s => s.Id).ShouldBe(trace.Sites.Select(s => s.Id));
        route.Cables.Select(c => c.Id).ShouldBe(trace.Cables.Select(c => c.Id));
        route.Cables.ShouldAllBe(c => c.Coordinates.Length >= 2);
        route.Extent.Length.ShouldBe(4);
        route.Sites.ShouldAllBe(s => s.X >= route.Extent[0] && s.X <= route.Extent[2] && s.Y >= route.Extent[1] && s.Y <= route.Extent[3]);
    }

    [Theory]
    [InlineData("/api/trace", HttpStatusCode.BadRequest)]
    [InlineData("/api/trace?service=1&circuit=1", HttpStatusCode.BadRequest)]
    [InlineData("/api/trace?service=999999999", HttpStatusCode.NotFound)]
    [InlineData("/api/trace?terminal=999999999", HttpStatusCode.NotFound)]
    public async Task Rejects_bad_requests_and_unknown_starts(string url, HttpStatusCode expected)
    {
        using var client = factory.CreateAuthenticatedClient();

        (await client.GetAsync(url, Ct)).StatusCode.ShouldBe(expected);
    }
}

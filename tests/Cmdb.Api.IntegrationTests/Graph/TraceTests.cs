using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Trace;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Graph;

/// <summary>Tracing (#9) against a generated network loaded into its own database and served by its own API.</summary>
public sealed class TraceTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_physical_circuit_is_the_physical_trace_from_its_first_terminal()
    {
        var network = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Embedded);
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
        await using var net = await NetworkApi.StartAsync(factory);
        var backhaul = net.Network.Services.First(s => s.Type == "mobile-backhaul");
        using var client = net.Client();

        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?service={backhaul.Id}", Ct))!;

        trace.Service!.Code.ShouldBe(backhaul.Code);
        trace.Circuits.Select(c => c.Layer).ShouldBe(["logical", "physical", "physical"], ignoreOrder: true);
        trace.Circuits.Single(c => c.Layer == "logical").Depth.ShouldBe(0);
        trace.Circuits.Where(c => c.Layer == "physical").ShouldAllBe(c => c.Depth == 1);
        trace.Circuits.SelectMany(c => c.Hops).ShouldAllBe(h => h.Kind != "unknown" && h.Site != null);
        trace.Sites.ShouldContain(s => s.Code.StartsWith("RAD-", StringComparison.Ordinal));
        trace.Sites.ShouldContain(s => s.Code.StartsWith("AGG-", StringComparison.Ordinal));
        trace.Cables.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Traces_the_physical_route_from_a_port_and_the_services_through_it()
    {
        await using var net = await NetworkApi.StartAsync(factory);
        var circuit = net.Network.Circuits.First(c => c.Layer == "physical" && c.Lifecycle == "in_service");
        using var client = net.Client();

        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={circuit.A}", Ct))!;

        trace.Physical!.Complete.ShouldBeTrue();
        trace.Physical.Hops[0].TerminalId.ShouldBe(circuit.A);
        trace.Physical.Hops[^1].TerminalId.ShouldBe(circuit.B);
        trace.Physical.Hops.Skip(1).ShouldAllBe(h => h.Edge != null);
        trace.Physical.Hops.ShouldContain(h => h.Edge == "conductor");
        trace.Services.ShouldNotBeEmpty();
        trace.ElapsedMs.ShouldBeLessThan(50);
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

    /// <summary>The API over a database holding the small generated network (seed 1).</summary>
    private sealed class NetworkApi : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _app;
        private readonly NpgsqlDataSource _db;

        private NetworkApi(WebApplicationFactory<Program> app, NpgsqlDataSource db, Network network)
        {
            _app = app;
            _db = db;
            Network = network;
        }

        public Network Network { get; }

        public static async Task<NetworkApi> StartAsync(ApiFactory factory)
        {
            var db = await factory.NewDatabaseAsync();
            var network = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Embedded);
            await Loader.LoadAsync(db, network, reset: false, TextWriter.Null, ct: Ct);
            // The data source hides the password; the API gets the full connection string.
            var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database }.ConnectionString;
            var app = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
            await app.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            return new NetworkApi(app, db, network);
        }

        public HttpClient Client()
        {
            var client = _app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token());
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _db.DisposeAsync();
        }
    }
}

using System.Net.Http.Json;
using Cmdb.Api.Features.Graph;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;

namespace Cmdb.Api.IntegrationTests.Graph;

public sealed class GraphLoaderTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_graph_loaded_from_Postgres_is_the_generated_network()
    {
        await using var db = await factory.NewDatabaseAsync();
        var network = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current);
        await Loader.LoadAsync(db, network, reset: false, TextWriter.Null, ct: Ct);

        var loaded = await GraphLoader.LoadAsync(db, Ct);
        var expected = GraphBuilder.Build(NetworkGraph.From(network), loaded.Version);

        Bytes(loaded).ShouldBe(Bytes(expected));
        loaded.NodeCount.ShouldBe((int)network.Terminals);
        loaded.EdgeCount.ShouldBe(network.Connections.Count + (int)network.Conductors);
        loaded.CircuitCount.ShouldBe(network.Circuits.Count);
    }

    [Fact]
    public async Task The_api_reports_the_loaded_graph()
    {
        using var client = factory.CreateAuthenticatedClient();

        var info = await client.GetFromJsonAsync<GraphInfoResponse>("/api/graph", Ct);

        info!.Ready.ShouldBeTrue();
        info.Source.ShouldBeOneOf("database", "reload"); // other tests write, which can trigger a reload
        info.Version.ShouldNotBeNullOrEmpty();
    }

    private static byte[] Bytes(Cmdb.Graph.Graph g)
    {
        using var stream = new MemoryStream();
        GraphSnapshot.Write(g, stream);
        return stream.ToArray();
    }
}

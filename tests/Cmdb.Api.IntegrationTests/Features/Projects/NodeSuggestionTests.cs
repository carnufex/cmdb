using System.Globalization;
using System.Net.Http.Json;
using Cmdb.Api.Features.Projects;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Projects;

/// <summary>Node placement (#294): a project's service points in clusters under the cap, each node at the centre or a manhole.</summary>
public sealed class NodeSuggestionTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Clusters_stay_under_the_cap_and_are_the_same_every_time()
    {
        var rng = new Random(1);
        var items = Enumerable.Range(1, 2000).Select(i => new NodeClustering.Item(i, 600_000 + (rng.NextDouble() * 6000), 6_900_000 + (rng.NextDouble() * 4000),
            i % 50 == 0 ? 24 : 1)).ToList();
        items.Add(new NodeClustering.Item(9999, 603_000, 6_902_000, 300));
        var clusters = NodeClustering.Cluster(items, 64);
        clusters.Sum(c => c.Count).ShouldBe(items.Count);
        clusters.Where(c => c.Count > 1).ShouldAllBe(c => c.Sum(p => p.Units) <= 64);
        clusters.ShouldContain(c => c.Count == 1 && c[0].Id == 9999);
        clusters.Count.ShouldBeLessThan((int)Math.Ceiling(items.Sum(p => p.Units) / 64.0) * 2);
        NodeClustering.Cluster(items, 64).Select(c => c.Select(p => p.Id).Order().First()).ShouldBe(clusters.Select(c => c.Select(p => p.Id).Order().First()));
    }

    [Fact]
    public async Task A_projects_service_points_get_nodes_at_their_centres_or_a_nearby_manhole_within_budget()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(66, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        // A village of 40 houses around a site where conduit meets, and 2 000 points spread around it.
        await using var find = db.CreateCommand("""
            SELECT s.id, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)) FROM site s
            WHERE EXISTS (SELECT 1 FROM route_segment r WHERE r.a_site_id = s.id) ORDER BY s.id LIMIT 1
            """);
        long manhole;
        double mx, my;
        await using (var reader = await find.ExecuteReaderAsync(Ct))
        {
            await reader.ReadAsync(Ct);
            (manhole, mx, my) = (reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2));
        }
        string N(double v) => v.ToString(CultureInfo.InvariantCulture);
        await using (var insert = db.CreateCommand($"""
            INSERT INTO service_point (code, type_key, geom)
            SELECT 'AP-V-' || n, 'villa', ST_SetSRID(ST_MakePoint({N(mx)} + 20 * (n % 8) - 70, {N(my)} + 20 * (n / 8) - 50), 3006) FROM generate_series(1, 40) n;
            INSERT INTO service_point (code, type_key, geom)
            SELECT 'AP-S-' || n, 'villa', ST_SetSRID(ST_MakePoint({N(mx)} + 3000 + (n * 7919 % 4000), {N(my)} - 2000 + (n * 104729 % 4000)), 3006) FROM generate_series(1, 2000) n;
            """))
        {
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var r = 400.0;
        var ring = new[] { new[] { mx - r, my - 2500 }, new[] { mx + 7500, my - 2500 }, new[] { mx + 7500, my + 2500 }, new[] { mx - r, my + 2500 } };
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Byarna", area = ring }, Ct)).Content.ReadFromJsonAsync<ProjectDetail>(Ct))!;

        var suggestions = await Cmdb.Api.IntegrationTests.Budget.WarmAsync(
            async () => (await client.GetFromJsonAsync<NodeSuggestions>($"/api/projects/{project.Id}/node-suggestions?capacity=64", Ct))!, s => s.ElapsedMs);
        var s = suggestions.Answer;
        s.ServicePoints.ShouldBe(2040);
        s.Nodes.Sum(n => n.ServicePoints).ShouldBe(2040);
        s.Nodes.ShouldAllBe(n => n.Units <= 64);
        s.Nodes.Count.ShouldBeGreaterThanOrEqualTo(32);
        // The village's node stands on the manhole in its middle; the others on the grid.
        s.Nodes.ShouldContain(n => n.SnappedTo != null && n.SnappedTo.Id == manhole);
        s.Nodes.Where(n => n.SnappedTo == null).ShouldAllBe(n => n.X % 25 == 0 && n.Y % 25 == 0);
        suggestions.FastestMs.ShouldBeLessThan(500);

        var bigger = (await client.GetFromJsonAsync<NodeSuggestions>($"/api/projects/{project.Id}/node-suggestions?capacity=128", Ct))!;
        bigger.Nodes.Count.ShouldBeLessThan(s.Nodes.Count);
    }
}

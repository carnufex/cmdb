using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Api.Features.Grid;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Plans;

/// <summary>
/// The spreadsheet mode (#27): a selection as rows with schema columns, attribute changes checked against the model's
/// schema inside a plan, and a lasso in the map.
/// </summary>
public sealed class SpreadsheetTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Attribute_changes_are_checked_against_the_schema_and_applied_with_the_plan()
    {
        var (db, api) = await NetworkAsync(61);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var switches = await Ids(db, """
            SELECT e.id FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id WHERE t.key = 'acme-ax-24' ORDER BY e.id LIMIT 3
            """);
        var site = await Scalar(db, $"SELECT site_id FROM equipment WHERE id = {switches[0]}");

        var grid = (await (await client.PostAsJsonAsync("/api/grid", new { kind = "equipment", siteIds = new[] { site } }, Ct))
            .Content.ReadFromJsonAsync<GridResult>(Ct))!;
        grid.Rows.ShouldContain(r => r.Id == switches[0] && r.TypeKey == "acme-ax-24");
        grid.Columns.Select(c => c.Key).ShouldContain("firmware");
        grid.Columns.Select(c => c.Key).ShouldContain("managementIp");

        var plan = await CreateAsync(client, "Uppgradera firmware");
        var batch = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations/batch", new
        {
            operations = switches.Select(id => new
            {
                kind = "set_attributes",
                type = "equipment",
                objectId = id,
                attributes = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["firmware"] = "9.9.1", ["serialNumber"] = null }),
            }),
        }, Ct);
        batch.StatusCode.ShouldBe(HttpStatusCode.OK, await batch.Content.ReadAsStringAsync(Ct));
        (await batch.Content.ReadFromJsonAsync<List<PlanOperationView>>(Ct))![0].Summary.ShouldContain("firmware = 9.9.1, serialNumber tas bort");

        // A key the schema does not allow, or a value of the wrong type, never enters the plan.
        foreach (var bad in new object[] { new { colour = "blå" }, new { firmware = 12 }, new { managementIp = "inte en adress" } })
        {
            var refused = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new
            {
                kind = "set_attributes",
                type = "equipment",
                objectId = switches[0],
                attributes = JsonSerializer.SerializeToElement(bad),
            }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("schema");
        }

        (await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Scalar(db, $"SELECT count(*) FROM equipment WHERE id = ANY(ARRAY[{string.Join(",", switches)}]) AND attributes->>'firmware' = '9.9.1' AND NOT attributes ? 'serialNumber'"))
            .ShouldBe(switches.Count);
    }

    [Fact]
    public async Task A_lasso_selects_the_sites_inside_it_and_scopes_apply_to_the_grid()
    {
        var (db, api) = await NetworkAsync(62);
        await using var dbScope = db;
        await using var apiScope = api;
        using var full = NetworkFixture.Client(api);
        using var region = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        // A box around the middle of Sweden, across the edge of region Nord.
        double[][] box = [[300_000, 6_800_000], [800_000, 6_800_000], [800_000, 7_100_000], [300_000, 7_100_000]];

        var all = (await (await full.PostAsJsonAsync("/api/sites/within", new { polygon = box }, Ct)).Content.ReadFromJsonAsync<WithinResult>(Ct))!;
        var north = (await (await region.PostAsJsonAsync("/api/sites/within", new { polygon = box }, Ct)).Content.ReadFromJsonAsync<WithinResult>(Ct))!;
        var expected = await Scalar(db, """
            SELECT count(*) FROM site WHERE ST_Intersects(geom, ST_MakeEnvelope(300000, 6800000, 800000, 7100000, 3006))
            """);
        all.Sites.Count.ShouldBe((int)expected);
        north.Sites.Count.ShouldBeLessThan(all.Sites.Count);

        var grid = (await (await region.PostAsJsonAsync("/api/grid", new { kind = "site", siteIds = all.Sites }, Ct))
            .Content.ReadFromJsonAsync<GridResult>(Ct))!;
        grid.Rows.Select(r => r.Id).ShouldBe(north.Sites, ignoreOrder: true);
        (await full.PostAsJsonAsync("/api/sites/within", new { polygon = new[] { new[] { 1.0, 2.0 } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> NetworkAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (db, api);
    }

    private static async Task<PlanSummary> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/plans", new { name }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
    }

    private static async Task<List<long>> Ids(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

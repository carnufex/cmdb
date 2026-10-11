using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Plans;

/// <summary>Distribution cables (#295): a tree from a node over distribution points, each cable sized for what lies beyond it.</summary>
public sealed class DistributionTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Each_cable_is_sized_for_the_units_beyond_it_with_the_reserve()
    {
        // Three distribution points in a row east of the node: the first cable carries all of them.
        var points = new[]
        {
            new DistributionPoint(1, "N-F01", 100, 0, 10, 10),
            new DistributionPoint(2, "N-F02", 200, 0, 12, 12),
            new DistributionPoint(3, "N-F03", 300, 50, 30, 30),
        };
        var warnings = new List<string>();
        var cables = Distribution.Tree(0, 0, null, points, 0.2, warnings);
        cables.Select(c => (c.From, c.To)).ShouldBe([(0, 1), (1, 2), (2, 3)]);
        cables.Select(c => c.Units).ShouldBe([52, 42, 30]);
        cables.Select(c => c.Fibres).ShouldBe([63, 51, 36]);
        cables.Select(c => c.TypeKey).ShouldBe(["fiber-96", "fiber-96", "fiber-48"]);
        cables[2].Way.ShouldHaveSingleItem().Kind.ShouldBe("point");
        cables[0].Way.ShouldBeEmpty();
        warnings.ShouldBeEmpty();

        Distribution.Tree(0, 0, null, [new DistributionPoint(1, "N-F01", 10, 10, 1, 400)], 0.2, warnings);
        warnings.ShouldHaveSingleItem().ShouldContain("480 fibrer");
    }

    [Fact]
    public async Task A_nodes_distribution_follows_conduit_where_there_is_some_and_goes_into_the_plan_as_drawn_cables()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(67, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        // A node at one end of two route segments in a row, a village by the far end, and houses far from any conduit.
        var row = await Row(db, """
            SELECT r1.a_site_id, r2.b_site_id, ST_X(ST_PointOnSurface(b.geom)), ST_Y(ST_PointOnSurface(b.geom))
            FROM route_segment r1 JOIN route_segment r2 ON r2.a_site_id = r1.b_site_id AND r2.b_site_id <> r1.a_site_id
            JOIN site b ON b.id = r2.b_site_id
            WHERE r1.length_m + r2.length_m BETWEEN 300 AND 3000 ORDER BY r1.id LIMIT 1
            """);
        var (node, far, fx, fy) = ((long)row[0]!, (long)row[1]!, (double)row[2]!, (double)row[3]!);
        string N(double v) => v.ToString(CultureInfo.InvariantCulture);
        await Exec(db, $"""
            INSERT INTO service_point (code, type_key, geom)
            SELECT 'AP-D-' || n, 'villa', ST_SetSRID(ST_MakePoint({N(fx)} + 10 * (n % 3), {N(fy)} + 10 * (n / 3)), 3006) FROM generate_series(1, 9) n
            """);
        var ids = await Ids(db, "SELECT id FROM service_point ORDER BY id");
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Fördelning i test" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt64();

        var dry = await PostAsync(client, plan, new { node, servicePoints = ids, dryRun = true });
        dry.Points.ShouldHaveSingleItem().Units.ShouldBe(9);
        var cable = dry.Cables.ShouldHaveSingleItem();
        cable.InConduitM.ShouldBeGreaterThan(0);
        cable.Way.Count(w => w.Kind == "segment").ShouldBe(2);
        (cable.Fibres, cable.TypeKey).ShouldBe((11, "fiber-12"));
        (await Count(db, $"SELECT count(*) FROM plan_operation WHERE plan_id = {plan}")).ShouldBe(0);

        var added = await PostAsync(client, plan, new { node, servicePoints = ids, dryRun = false });
        added.Operations.ShouldBe(2);
        (await Count(db, $"SELECT count(*) FROM plan_operation WHERE plan_id = {plan} AND kind = 'create_cable' AND payload ? 'steps'")).ShouldBe(1);
        var view = await client.GetFromJsonAsync<JsonElement>($"/api/plans/{plan}/view", Ct);
        view.GetProperty("problems").GetInt32().ShouldBe(0);

        // Far from any conduit: on the grid, with a break point where it turns.
        await Exec(db, $"""
            INSERT INTO service_point (code, type_key, geom)
            SELECT 'AP-G-' || n, 'villa', ST_SetSRID(ST_MakePoint({N(fx)} + 20000 + 10 * n, {N(fy)} + 15000), 3006) FROM generate_series(1, 5) n
            """);
        var grid = await PostAsync(client, plan, new { node, servicePoints = await Ids(db, "SELECT id FROM service_point WHERE code LIKE 'AP-G-%'"), dryRun = true });
        grid.Cables.ShouldAllBe(c => c.InConduitM == 0);
        grid.Cables.ShouldContain(c => c.Way.Any(w => w.Kind == "point"));

        (await client.PostAsJsonAsync($"/api/plans/{plan}/distribution", new { node = 999_999_999, servicePoints = ids }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // A reader may look; only a planner may add.
        using var reader = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await reader.PostAsJsonAsync($"/api/plans/{plan}/distribution", new { node, servicePoints = ids, dryRun = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        far.ShouldBeGreaterThan(0);
    }

    private static async Task<DistributionPlan> PostAsync(HttpClient client, long plan, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/plans/{plan}/distribution", body, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<DistributionPlan>(Ct))!;
    }

    private static async Task<object?[]> Row(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        var row = new object?[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return row;
    }

    private static async Task<long[]> Ids(NpgsqlDataSource db, string sql)
    {
        var ids = new List<long>();
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return [.. ids];
    }

    private static async Task<long> Count(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}

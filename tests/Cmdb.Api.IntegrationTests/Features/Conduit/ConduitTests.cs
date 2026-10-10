using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Conduit;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Conduit;

/// <summary>The conduit in the map, a route segment with its ducts and tubes, and a cable's way through them (#236, ADR-0014).</summary>
public sealed class ConduitTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_route_segment_shows_its_ducts_and_tubes_and_a_cable_its_way_within_the_callers_scopes()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(61, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        // A corridor shared by several cables, and a cable on it.
        var segment = await Scalar(db, """
            SELECT ds.route_segment_id FROM cable_path p JOIN subduct s ON s.id = p.subduct_id JOIN duct_segment ds ON ds.duct_id = s.duct_id
            GROUP BY ds.route_segment_id ORDER BY count(*) DESC, ds.route_segment_id LIMIT 1
            """);
        var detail = (await client.GetFromJsonAsync<RouteSegmentDetail>($"/api/route-segments/{segment}", Ct))!;
        detail.Ducts.ShouldNotBeEmpty();
        detail.Ducts.ShouldAllBe(d => d.Subducts.Count == (d.TypeKey == "acme-md-7x16" ? 7 : d.TypeKey == "acme-md-24x7" ? 24 : 1));
        var taken = detail.Ducts.SelectMany(d => d.Subducts).Where(s => s.Occupancy == "cable").ToList();
        taken.Count.ShouldBeGreaterThan(1);
        taken.ShouldAllBe(s => s.Cable != null && s.Cable.Id > 0);
        detail.FreeSubducts.ShouldBe(detail.Ducts.SelectMany(d => d.Subducts).Count(s => s.Occupancy == "empty"));
        detail.ElapsedMs.ShouldBeLessThan(500);

        var cable = taken[0].Cable!.Id;
        var path = (await client.GetFromJsonAsync<List<CablePathStep>>($"/api/cables/{cable}/path", Ct))!;
        path.Select(p => p.Seq).ShouldBe(Enumerable.Range(0, path.Count));
        path.ShouldContain(p => p.Segment.Id == segment);

        // Tiles: the conduit's own layer, with corridors at a national zoom.
        var tile = await client.GetAsync("/api/tiles/conduit/2/1/1", Ct);
        tile.StatusCode.ShouldBe(HttpStatusCode.OK);
        var anyTile = false;
        for (var x = 0; x < 4 && !anyTile; x++)
        {
            for (var y = 0; y < 4 && !anyTile; y++)
            {
                anyTile = (await client.GetByteArrayAsync($"/api/tiles/conduit/2/{x}/{y}", Ct)).Length > 0;
            }
        }
        anyTile.ShouldBeTrue();
        (await client.GetAsync("/api/route-segments/999999999", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A regional reader sees segments that cross its area, and a cable outside it in a tube only as taken.
        using var regional = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var inside = await Scalar(db, "SELECT min(route_segment_id) FROM scope_route_segment WHERE scope_key = 'region-nord'");
        var outside = await Scalar(db, """
            SELECT min(r.id) FROM route_segment r WHERE NOT EXISTS (SELECT 1 FROM scope_route_segment z WHERE z.scope_key = 'region-nord' AND z.route_segment_id = r.id)
            """);
        (await regional.GetAsync($"/api/route-segments/{inside}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await regional.GetAsync($"/api/route-segments/{outside}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var hiddenCable = await db.CreateCommand("""
            SELECT ds.route_segment_id FROM duct_segment ds JOIN subduct s ON s.duct_id = ds.duct_id JOIN cable_path p ON p.subduct_id = s.id
            JOIN scope_route_segment z ON z.route_segment_id = ds.route_segment_id AND z.scope_key = 'region-nord'
            WHERE NOT EXISTS (SELECT 1 FROM scope_cable c WHERE c.scope_key = 'region-nord' AND c.cable_id = p.cable_id)
            ORDER BY 1 LIMIT 1
            """).ExecuteScalarAsync(Ct);
        if (hiddenCable is long crossing)
        {
            var seen = (await regional.GetFromJsonAsync<RouteSegmentDetail>($"/api/route-segments/{crossing}", Ct))!;
            seen.Ducts.SelectMany(d => d.Subducts).ShouldContain(s => s.Occupancy == "cable" && s.Cable!.Id == 0);
        }
    }

    [Fact]
    public async Task A_dig_across_a_route_segment_cuts_every_cable_in_its_ducts_and_a_planned_dig_shows_the_segments_it_crosses()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(62, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        // The corridor with the most cables carrying services.
        var segment = await Scalar(db, """
            SELECT ds.route_segment_id FROM cable_path p JOIN subduct s ON s.id = p.subduct_id JOIN duct_segment ds ON ds.duct_id = s.duct_id
            WHERE EXISTS (SELECT 1 FROM conductor k JOIN conductor_end e ON e.conductor_id = k.id JOIN circuit_hop h ON h.terminal_id = e.terminal_id WHERE k.cable_id = p.cable_id)
            GROUP BY ds.route_segment_id ORDER BY count(*) DESC, ds.route_segment_id LIMIT 1
            """);
        var impact = (await client.GetFromJsonAsync<Cmdb.Api.Features.Objects.Impact>($"/api/route-segments/{segment}/impact", Ct))!;
        impact.Cables!.Count.ShouldBeGreaterThan(1);
        impact.ElapsedMs.ShouldBeLessThan(200);

        // The same as cutting each of its cables, together.
        var services = new HashSet<long>();
        foreach (var cable in impact.Cables)
        {
            var one = (await client.GetFromJsonAsync<Cmdb.Api.Features.Objects.Impact>($"/api/cables/{cable.Id}/impact", Ct))!;
            services.UnionWith(one.Services.Select(s => s.Service.Id));
        }
        impact.Services.Select(s => s.Service.Id).ToHashSet().SetEquals(services).ShouldBeTrue();
        services.Count.ShouldBeGreaterThan(0);

        (await client.GetAsync("/api/route-segments/999999999/impact", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A planned dig across the segment lists it.
        await using (var cmd = db.CreateCommand($"""
            INSERT INTO planned_work (title, description, contractor, responsible_employee_id, area, starts_at, ends_at)
            SELECT 'Schaktning', 'Test', 'Exempel AB', '1001', ST_Buffer(ST_LineInterpolatePoint(geom, 0.5), 20), now(), now() + interval '2 days'
            FROM route_segment WHERE id = {segment}
            """))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var works = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/operations/works", Ct);
        works.GetProperty("works").EnumerateArray().ShouldContain(w =>
            w.GetProperty("routeSegments").EnumerateArray().Any(s => s.GetProperty("id").GetInt64() == segment));
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Plans;

/// <summary>
/// A new cable drawn in the map (#259): along route segments in empty tubes, with new stretches that become new route
/// segments, its length from the drawing, warnings for full tubes, and applied into the conduit. Its own network, since
/// applying fills tubes.
/// </summary>
public sealed class CableRouteTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cable_drawn_along_segments_and_a_new_stretch_takes_tubes_and_adds_a_segment()
    {
        var (db, api) = await NetworkAsync(41);
        await using var _db = db;
        await using var _api = api;
        using var client = NetworkFixture.Client(api);

        // Two segments end to end through a site, both with empty tubes, and a site with no segment to the far end.
        await using var cmd = db.CreateCommand("""
            WITH free AS (
                SELECT r.id, r.a_site_id, r.b_site_id, r.length_m FROM route_segment r
                WHERE EXISTS (SELECT 1 FROM duct_segment ds JOIN subduct s ON s.duct_id = ds.duct_id WHERE ds.route_segment_id = r.id AND s.occupancy = 'empty'))
            SELECT f1.id, f2.id, f1.a_site_id, f1.b_site_id, f2.b_site_id, f1.length_m + f2.length_m,
                   (SELECT s.id FROM site s WHERE s.id NOT IN (f1.a_site_id, f1.b_site_id, f2.b_site_id)
                      AND NOT EXISTS (SELECT 1 FROM route_segment x WHERE (x.a_site_id, x.b_site_id) IN ((s.id, f2.b_site_id), (f2.b_site_id, s.id)))
                    ORDER BY s.geom <-> (SELECT geom FROM site WHERE id = f2.b_site_id) LIMIT 1)
            FROM free f1 JOIN free f2 ON f2.a_site_id = f1.b_site_id AND f2.b_site_id <> f1.a_site_id
            ORDER BY f1.id LIMIT 1
            """);
        long seg1, seg2, a, middle, b, c;
        double alongSegments;
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            (seg1, seg2, a, middle, b, alongSegments, c) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetDouble(5), reader.GetInt64(6));
        }
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Ny kabel längs trasé" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;

        // The preview follows the segments in empty tubes, then a new stretch over a break point to the third site.
        var (bx, by) = await Point(db, b);
        var (cx, cy) = await Point(db, c);
        object[] steps =
        [
            new { kind = "segment", id = seg1 },
            new { kind = "segment", id = seg2 },
            new { kind = "point", x = (bx + cx) / 2 + 50, y = (by + cy) / 2 },
            new { kind = "site", id = c, construction = "aerial" },
        ];
        var preview = (await (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/cable-route", new { aSiteId = a, steps }, Ct))
            .Content.ReadFromJsonAsync<CableRoute>(Ct))!;
        preview.Complete.ShouldBeTrue();
        preview.End!.Id.ShouldBe(c);
        preview.InConduitM.ShouldBe(Math.Round(alongSegments), 2);
        preview.NewM.ShouldBeGreaterThan(0);
        preview.LengthM.ShouldBe(preview.InConduitM + preview.NewM, 2);
        preview.Legs.Where(l => l.Kind == "segment").ShouldAllBe(l => l.Subduct != null);
        preview.Legs.Select(l => l.ToSite).ShouldBe([middle, b, null, c]);
        preview.Warnings.ShouldBeEmpty();
        // Not to the far end it was not drawn to.
        var wrongEnd = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations",
            new { kind = "create_cable", aSiteId = a, bSiteId = b, typeKey = "fiber-12", route = steps }, Ct);
        wrongEnd.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var op = (await (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations",
            new { kind = "create_cable", aSiteId = a, bSiteId = c, typeKey = "fiber-12", route = steps }, Ct)).Content.ReadFromJsonAsync<PlanOperationView>(Ct))!;
        op.Problem.ShouldBeNull();
        // The plan's map draws the cable along the drawing.
        var view = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        view.Planned!.Cables.Single().Coordinates.Length.ShouldBe(preview.Coordinates.Length);
        // A second cable the same way gets other tubes: the first one's are taken in the plan.
        var again = (await (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/cable-route", new { aSiteId = a, steps }, Ct))
            .Content.ReadFromJsonAsync<CableRoute>(Ct))!;
        again.Legs.Where(l => l.Kind == "segment").Select(l => l.Subduct)
            .Intersect(preview.Legs.Where(l => l.Kind == "segment").Select(l => l.Subduct)).ShouldBeEmpty();

        // Drawn again without the break point: the same operation, a straight new stretch, the plan's map follows.
        object[] straight = [steps[0], steps[1], steps[3]];
        var redrawn = (await (await client.PutAsJsonAsync($"/api/plans/{plan.Id}/operations/{op.Id}/route", new { route = straight }, Ct))
            .Content.ReadFromJsonAsync<CableRoute>(Ct))!;
        redrawn.Coordinates.Length.ShouldBe(preview.Coordinates.Length - 1);
        redrawn.Legs.Where(l => l.Kind == "segment").Select(l => l.Subduct).ShouldBe(preview.Legs.Where(l => l.Kind == "segment").Select(l => l.Subduct));
        (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!.Planned!.Cables.Single().Coordinates.Length.ShouldBe(redrawn.Coordinates.Length);
        (await client.PutAsJsonAsync($"/api/plans/{plan.Id}/operations/{op.Id}/route", new { route = new[] { steps[0] } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tubes = preview.Legs.Where(l => l.Subduct != null).Select(l => l.Subduct!.Value).Distinct().ToArray();
        var cable = await Scalar(db, $"SELECT cable_id FROM cable_path WHERE subduct_id = {tubes[0]}");
        (await Scalar(db, $"SELECT count(*) FROM cable_path WHERE cable_id = {cable}")).ShouldBe(tubes.Length);
        (await Scalar(db, $"SELECT count(*) FROM subduct WHERE id = ANY(ARRAY[{string.Join(",", tubes)}]) AND occupancy = 'cable'")).ShouldBe(tubes.Length);
        (await Scalar(db, $"SELECT (a_site_id = {a} AND b_site_id = {c} AND abs(length_m - {redrawn.LengthM.ToString(System.Globalization.CultureInfo.InvariantCulture)}) < 1)::int FROM cable WHERE id = {cable}")).ShouldBe(1);
        // The new stretch is a planned, hung route segment from the far end of the second segment to the third site.
        (await Scalar(db, $"""
            SELECT count(*) FROM route_segment
            WHERE a_site_id = {b} AND b_site_id = {c} AND construction = 'aerial' AND lifecycle = 'planned' AND code LIKE 'TR-%' AND ST_NPoints(geom) = 2
            """)).ShouldBe(1);
    }

    [Fact]
    public async Task A_full_segment_is_warned_about_with_a_way_round_it()
    {
        var (db, api) = await NetworkAsync(42);
        await using var _db = db;
        await using var _api = api;
        using var client = NetworkFixture.Client(api);
        var (segment, a) = await Pair(db, """
            SELECT r.id, r.a_site_id FROM route_segment r
            WHERE EXISTS (SELECT 1 FROM duct_segment ds WHERE ds.route_segment_id = r.id) ORDER BY r.id LIMIT 1
            """);
        // Every tube on it taken.
        await Exec(db, $"UPDATE subduct SET occupancy = 'cable' WHERE duct_id IN (SELECT duct_id FROM duct_segment WHERE route_segment_id = {segment})");
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Full dukt" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;

        var full = (await (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/cable-route",
            new { aSiteId = a, steps = new object[] { new { kind = "segment", id = segment } } }, Ct)).Content.ReadFromJsonAsync<CableRoute>(Ct))!;
        full.Legs.Single().Subduct.ShouldBeNull();
        full.Warnings.ShouldHaveSingleItem().ShouldContain("upptagna");
        // Beside it: a new trench along the same way, no tubes and no warning.
        var beside = (await (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/cable-route",
            new { aSiteId = a, steps = new object[] { new { kind = "segment", id = segment, beside = true } } }, Ct)).Content.ReadFromJsonAsync<CableRoute>(Ct))!;
        (beside.Legs.Single().Kind, beside.Legs.Single().Construction, beside.Warnings.Count).ShouldBe(("new", "trench", 0));
        beside.NewM.ShouldBe(full.InConduitM, 2);

        (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/cable-route", new { aSiteId = 999999999, steps = new object[] { new { kind = "point", x = 1, y = 1 } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> NetworkAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        await Exec(db, "DELETE FROM plan_operation; DELETE FROM plan_dependency; DELETE FROM plan");
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (db, api);
    }

    private static async Task<(double X, double Y)> Point(NpgsqlDataSource db, long site)
    {
        await using var cmd = db.CreateCommand($"SELECT ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site WHERE id = {site}");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetDouble(0), reader.GetDouble(1));
    }

    private static async Task<(long, long)> Pair(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}

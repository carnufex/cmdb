using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Fibre;
using Cmdb.Api.Features.Plans;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Fibre;

/// <summary>
/// The fibre drawing (#254): a cable fibre by fibre with what each end is spliced or terminated to and what it carries,
/// the distances from a break point, route segments, sites and selections, a plan's view, and the caller's scope.
/// </summary>
public sealed class FibreDrawingTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cable_is_drawn_fibre_by_fibre_with_its_ends_what_it_carries_and_a_break_point()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        // The cable carrying the most circuits.
        var cable = await Scalar(db, """
            SELECT k.cable_id FROM conductor k JOIN conductor_end e ON e.conductor_id = k.id JOIN circuit_hop h ON h.terminal_id = e.terminal_id
            GROUP BY k.cable_id ORDER BY count(DISTINCT h.circuit_id) DESC, k.cable_id LIMIT 1
            """);
        var (mx, my) = await Point(db, $"SELECT ST_X(p), ST_Y(p) FROM (SELECT ST_LineInterpolatePoint(geom, 0.25) AS p FROM cable WHERE id = {cable}) c");

        // The budget is for warm calls (docs/plan.md), as in #273: the first one pays for JIT.
        var url = FormattableString.Invariant($"/api/fibre/cables/{cable}?x={mx}&y={my}");
        await client.GetFromJsonAsync<FibreDrawing>(url, Ct);
        var drawing = (await client.GetFromJsonAsync<FibreDrawing>(url, Ct))!;
        var c = drawing.Cables.ShouldHaveSingleItem();
        c.Fibres.Count.ShouldBe((int)await Scalar(db, $"SELECT count(*) FROM conductor WHERE cable_id = {cable}"));
        c.Fibres.Select(f => f.Number).ShouldBe(c.Fibres.Select(f => f.Number).Order());
        // A lit fibre is spliced or terminated somewhere and carries circuits, and its services are listed with the drawing.
        var lit = c.Fibres.First(f => f.Circuits.Count > 0);
        lit.Usage.ShouldBe("lit");
        (lit.A.Count + lit.B.Count).ShouldBeGreaterThan(0);
        lit.A.Concat(lit.B).ShouldAllBe(h => h.Kind == "port" || h.Kind == "conductor_end");
        drawing.Services.Count.ShouldBeGreaterThan(0);
        lit.Services.ShouldBeLessThanOrEqualTo(drawing.Services.Count);
        // A quarter of the way along: three times as far to B as to A.
        var brk = drawing.Break.ShouldNotBeNull();
        (brk.ToA + brk.ToB).ShouldBeGreaterThan(0);
        (brk.ToB / Math.Max(1, brk.ToA)).ShouldBeInRange(2.9, 3.1);
        drawing.ElapsedMs.ShouldBeLessThan(200);

        (await client.GetAsync("/api/fibre/cables/999999999", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/fibre/toasters/1", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Route_segments_sites_and_selections_draw_their_cables_inside_the_scope()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        var segment = await Scalar(db, """
            SELECT ds.route_segment_id FROM cable_path p JOIN subduct s ON s.id = p.subduct_id JOIN duct_segment ds ON ds.duct_id = s.duct_id
            GROUP BY ds.route_segment_id ORDER BY count(DISTINCT p.cable_id) DESC, ds.route_segment_id LIMIT 1
            """);
        var inSegment = await Scalar(db, $"""
            SELECT count(DISTINCT p.cable_id) FROM cable_path p JOIN subduct s ON s.id = p.subduct_id JOIN duct_segment ds ON ds.duct_id = s.duct_id
            WHERE ds.route_segment_id = {segment}
            """);
        // Warm: the first call also builds the conduit index for the graph (#237).
        await client.GetFromJsonAsync<FibreDrawing>($"/api/fibre/route-segments/{segment}", Ct);
        var bySegment = (await client.GetFromJsonAsync<FibreDrawing>($"/api/fibre/route-segments/{segment}", Ct))!;
        bySegment.Cables.Count.ShouldBe((int)Math.Min(inSegment, FibreDrawings.MaxCables));
        bySegment.Title.ShouldStartWith("Trasésträcka");
        bySegment.Cables.Select(x => x.Cable.Code).ShouldBe(bySegment.Cables.Select(x => x.Cable.Code).Order(StringComparer.Ordinal));
        bySegment.ElapsedMs.ShouldBeLessThan(200);

        var site = await Scalar(db, "SELECT a_site_id FROM cable GROUP BY a_site_id ORDER BY count(*) DESC, a_site_id LIMIT 1");
        var bySite = (await client.GetFromJsonAsync<FibreDrawing>($"/api/fibre/sites/{site}", Ct))!;
        bySite.Cables.ShouldAllBe(x => x.A.Id == site || x.B.Id == site);

        var first = bySite.Cables[0];
        var selection = await client.PostAsJsonAsync("/api/fibre/selection", new { siteIds = new[] { first.A.Id, first.B.Id }, cableIds = Array.Empty<long>() }, Ct);
        (await selection.Content.ReadFromJsonAsync<FibreDrawing>(Ct))!.Cables.ShouldContain(x => x.Cable.Id == first.Cable.Id);

        // Someone who sees the north only: a southern cable does not exist for them, and a segment's other cables are counted.
        using var north = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var south = await Scalar(db, """
            SELECT c.id FROM cable c JOIN site a ON a.id = c.a_site_id JOIN site b ON b.id = c.b_site_id
            WHERE ST_Y(ST_PointOnSurface(a.geom)) < 6400000 AND ST_Y(ST_PointOnSurface(b.geom)) < 6400000 ORDER BY c.id LIMIT 1
            """);
        (await north.GetAsync($"/api/fibre/cables/{south}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_plan_draws_the_intervention_before_and_after()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        // A fibre with a free end at a site with a free port, so a plan can terminate it.
        await using var cmd = db.CreateCommand("""
            SELECT k.cable_id, k.number, e.terminal_id, p.terminal_id
            FROM conductor k JOIN conductor_end e ON e.conductor_id = k.id AND e.side = 'A' JOIN cable c ON c.id = k.cable_id
            JOIN equipment q ON q.site_id = c.a_site_id JOIN port p ON p.equipment_id = q.id
            WHERE NOT EXISTS (SELECT 1 FROM connection x WHERE e.terminal_id IN (x.a_terminal_id, x.b_terminal_id) AND x.valid_to IS NULL)
              AND NOT EXISTS (SELECT 1 FROM connection x WHERE p.terminal_id IN (x.a_terminal_id, x.b_terminal_id) AND x.valid_to IS NULL)
            ORDER BY k.cable_id, k.number, p.terminal_id LIMIT 1
            """);
        long cable, end, port;
        int number;
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
        {
            await reader.ReadAsync(Ct);
            (cable, number, end, port) = (reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3));
        }
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Fiberritning" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        try
        {
            (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "connect", a = end, b = port, connectionKind = "termination" }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            var before = (await client.GetFromJsonAsync<FibreDrawing>($"/api/fibre/cables/{cable}", Ct))!.Cables[0].Fibres.Single(f => f.Number == number);
            var after = (await client.GetFromJsonAsync<FibreDrawing>($"/api/fibre/cables/{cable}?plan={plan.Id}", Ct))!.Cables[0].Fibres.Single(f => f.Number == number);
            before.A.ShouldBeEmpty();
            after.A.ShouldHaveSingleItem().TerminalId.ShouldBe(port);
        }
        finally
        {
            await Exec(db, $"DELETE FROM reservation WHERE holder_id = {plan.Id}; DELETE FROM plan_operation WHERE plan_id = {plan.Id}; DELETE FROM plan WHERE id = {plan.Id}");
        }
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(double X, double Y)> Point(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetDouble(0), reader.GetDouble(1));
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}

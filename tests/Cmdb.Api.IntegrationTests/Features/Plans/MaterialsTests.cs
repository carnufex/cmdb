using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Plans;

/// <summary>
/// A plan's materials (#291): cables by type and length, as drawn along the conduit or straight, equipment by model, new
/// route segments by construction and metres in existing conduit, priced from the catalog.
/// </summary>
public sealed class MaterialsTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plans_materials_are_its_cables_equipment_and_conduit_priced_from_the_catalog()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        // A segment with an empty tube, the site at its far end, and another site with no segment to it.
        await using var cmd = db.CreateCommand("""
            SELECT r.id, r.a_site_id, r.b_site_id, r.length_m,
                   (SELECT s.id FROM site s WHERE s.id NOT IN (r.a_site_id, r.b_site_id)
                      AND NOT EXISTS (SELECT 1 FROM route_segment x WHERE (x.a_site_id, x.b_site_id) IN ((s.id, r.b_site_id), (r.b_site_id, s.id)))
                    ORDER BY s.geom <-> (SELECT geom FROM site WHERE id = r.b_site_id) LIMIT 1)
            FROM route_segment r
            WHERE EXISTS (SELECT 1 FROM duct_segment ds JOIN subduct s ON s.duct_id = ds.duct_id WHERE ds.route_segment_id = r.id AND s.occupancy = 'empty')
            ORDER BY r.id LIMIT 1
            """);
        long segment, a, b, c;
        double segmentLength;
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
        {
            await reader.ReadAsync(Ct);
            (segment, a, b, segmentLength, c) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetInt64(4));
        }
        var straight = await Scalar(db, $"SELECT round(ST_Distance(ST_PointOnSurface(x.geom), ST_PointOnSurface(y.geom))) FROM site x, site y WHERE x.id = {a} AND y.id = {c}");
        var odf = TypeCatalog.Current.Find("acme-odf-96")!;

        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Material" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        try
        {
            async Task Add(object op) =>
                (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", op, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            await Add(new { kind = "create_equipment", siteId = a, typeKey = odf.Key, name = "ODF ny" });
            await Add(new { kind = "create_equipment", siteId = b, typeKey = odf.Key, name = "ODF ny 2" });
            await Add(new { kind = "create_cable", aSiteId = a, bSiteId = c, typeKey = "fiber-24" });
            await Add(new
            {
                kind = "create_cable",
                aSiteId = a,
                bSiteId = c,
                typeKey = "fiber-24",
                route = new object[] { new { kind = "segment", id = segment }, new { kind = "site", id = c } },
            });

            var materials = (await client.GetFromJsonAsync<PlanMaterials>($"/api/plans/{plan.Id}/materials", Ct))!;
            materials.Currency.ShouldBe("SEK");
            materials.Missing.ShouldBeEmpty();
            var odfs = materials.Lines.Single(l => l.Kind == "equipment");
            (odfs.Key, odfs.Quantity, odfs.Unit, odfs.UnitCost).ShouldBe((odf.Key, 2m, "st", odf.Cost));
            // Both cables of the same type on one line: straight between the sites, and as drawn.
            var cable = materials.Lines.Single(l => l.Kind == "cable");
            var drawnNew = await Scalar(db, $"SELECT round(ST_Distance(ST_PointOnSurface(x.geom), ST_PointOnSurface(y.geom))) FROM site x, site y WHERE x.id = {b} AND y.id = {c}");
            cable.Quantity.ShouldBe((decimal)(straight + Math.Round(segmentLength) + drawnNew), 3m);
            cable.UnitCost.ShouldBe(TypeCatalog.Current.FindCable("fiber-24")!.CostPerMeter);
            // The drawn cable pulled through the segment's tube, and a new dug stretch from its far end.
            var conduit = materials.Lines.Where(l => l.Kind == "construction").ToDictionary(l => l.Key);
            conduit["existing"].Quantity.ShouldBe((decimal)Math.Round(segmentLength), 3m);
            conduit["trench"].Quantity.ShouldBe((decimal)drawnNew, 1m);
            conduit["trench"].UnitCost.ShouldBe(TypeCatalog.Current.Costs.Construction["trench"]);
            materials.Total.ShouldBe(materials.Lines.Sum(l => l.Cost ?? 0));
            materials.Total.ShouldBeGreaterThan(0);

            (await client.GetAsync("/api/plans/999999999/materials", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await using var clean = db.CreateCommand($"DELETE FROM plan_operation WHERE plan_id = {plan.Id}; DELETE FROM plan WHERE id = {plan.Id}");
            await clean.ExecuteNonQueryAsync(Ct);
        }
    }

    private static async Task<double> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToDouble(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

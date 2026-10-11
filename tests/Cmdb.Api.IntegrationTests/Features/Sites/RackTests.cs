using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Api.Features.Sites;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Sites;

/// <summary>
/// The rack view's rack (#255): its equipment on its units with the models' pictures, and in a plan the equipment
/// the plan puts there and what it removes.
/// </summary>
public sealed class RackTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rack_shows_its_equipment_and_a_plans_changes_to_it()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        // The rack with the most rack-mounted equipment.
        var rackId = await Scalar(db, """
            SELECT l.id FROM location l JOIN equipment e ON e.location_id = l.id AND e.rack_position IS NOT NULL
            WHERE l.kind = 'rack' GROUP BY l.id ORDER BY count(*) DESC, l.id LIMIT 1
            """);
        var rack = (await client.GetFromJsonAsync<RackDetail>($"/api/racks/{rackId}", Ct))!;
        rack.Units.ShouldBeGreaterThan(0);
        rack.Equipment.ShouldNotBeEmpty();
        rack.Equipment.ShouldAllBe(e => e.Units >= 1);
        rack.Equipment.Count.ShouldBe((int)await Scalar(db,
            $"SELECT count(*) FROM equipment WHERE location_id = {rackId} AND parent_id IS NULL AND lifecycle <> 'removed'"));
        (await client.GetAsync("/api/racks/999999999", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A plan that puts a switch in the rack and removes something from it.
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Rackvy" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        try
        {
            // Something in the rack that carries no circuits, so the plan may remove it.
            var removedId = await Scalar(db, $"""
                SELECT min(e.id) FROM equipment e WHERE e.location_id = {rackId} AND e.parent_id IS NULL AND e.rack_position IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM port p JOIN circuit_hop h ON h.terminal_id = p.terminal_id
                                  JOIN equipment x ON x.id = p.equipment_id WHERE x.id = e.id OR x.parent_id = e.id)
                """);
            (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations",
                new { kind = "create_equipment", siteId = rack.Site.Id, typeKey = "acme-ax-24", name = "RACKVY-SW-1", rack = rack.Name }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations",
                new { kind = "remove", type = "equipment", objectId = removedId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            var planned = (await client.GetFromJsonAsync<RackDetail>($"/api/racks/{rackId}?plan={plan.Id}", Ct))!;
            planned.Planned.ShouldHaveSingleItem().Name.ShouldBe("RACKVY-SW-1");
            planned.Planned[0].Id.ShouldBeLessThan(0);
            planned.Removed.ShouldBe([removedId]);
            // Production stays as it is.
            (await client.GetFromJsonAsync<RackDetail>($"/api/racks/{rackId}", Ct))!.Planned.ShouldBeEmpty();
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

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}

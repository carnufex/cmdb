using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Api.Features.Sites;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Sites;

/// <summary>
/// The room view (#256): a room's racks in their rows with how full they are, the highest criticality in each and the
/// connections between them; a plan's new racks and new places; and the caller's scope.
/// </summary>
public sealed class RoomTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_room_shows_its_racks_in_rows_with_free_units_criticality_and_links()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        // A hub's node room: six racks in two rows of three, the second row turned round (the generator's placing).
        var roomId = await Scalar(db, """
            SELECT m.id FROM location m JOIN location r ON r.parent_id = m.id AND r.kind = 'rack'
            WHERE m.kind = 'room' GROUP BY m.id HAVING count(*) = 6 ORDER BY m.id LIMIT 1
            """);
        var room = (await client.GetFromJsonAsync<RoomDetail>($"/api/rooms/{roomId}", Ct))!;
        room.Racks.Count.ShouldBe(6);
        room.Racks.Select(r => (r.Row, r.Slot)).ShouldBe([(1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (2, 3)]);
        room.Racks.Where(r => r.Row == 1).ShouldAllBe(r => r.Facing == "front");
        room.Racks.Where(r => r.Row == 2).ShouldAllBe(r => r.Facing == "back");
        room.Racks.ShouldAllBe(r => r.FreeUnits >= 0 && r.FreeUnits <= r.Units);
        var busiest = room.Racks.MaxBy(r => r.Equipment)!;
        busiest.FreeUnits.ShouldBe((int)await Scalar(db, $"""
            SELECT coalesce(r.rack_units, 42) - coalesce(sum(coalesce(t.rack_units, 1)) FILTER (WHERE e.rack_position IS NOT NULL), 0)
            FROM location r LEFT JOIN equipment e ON e.location_id = r.id AND e.parent_id IS NULL AND e.lifecycle <> 'removed'
            LEFT JOIN equipment_type t ON t.id = e.equipment_type_id WHERE r.id = {busiest.Id} GROUP BY r.id
            """));
        // The connections between the room's racks, each pair once.
        room.Links.Sum(l => l.Connections).ShouldBe((int)await Scalar(db, $"""
            SELECT count(*) FROM connection c
            JOIN port pa ON pa.terminal_id = c.a_terminal_id JOIN equipment ea ON ea.id = pa.equipment_id
            JOIN port pb ON pb.terminal_id = c.b_terminal_id JOIN equipment eb ON eb.id = pb.equipment_id
            JOIN location la ON la.id = ea.location_id JOIN location lb ON lb.id = eb.location_id
            WHERE c.valid_to IS NULL AND la.parent_id = {roomId} AND lb.parent_id = {roomId} AND la.kind = 'rack' AND lb.kind = 'rack' AND la.id <> lb.id
            """));
        room.Links.ShouldAllBe(l => l.A < l.B);
        room.Building.ShouldNotBeNull();

        var rooms = (await client.GetFromJsonAsync<List<SiteRoom>>($"/api/sites/{room.Site.Id}/rooms", Ct))!;
        var listed = rooms.Single(r => r.Id == roomId);
        (listed.Racks, listed.Units, listed.FreeUnits).ShouldBe((6, room.Racks.Sum(r => r.Units), room.Racks.Sum(r => r.FreeUnits)));
        (await client.GetAsync("/api/rooms/999999999", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_plan_adds_racks_and_places_them_and_a_taken_place_is_refused()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        var (roomId, siteId) = await Pair(db, """
            SELECT m.id, m.site_id FROM location m JOIN location r ON r.parent_id = m.id AND r.kind = 'rack'
            WHERE m.kind = 'room' GROUP BY m.id HAVING count(*) = 3 ORDER BY m.id LIMIT 1
            """);
        var racks = (await client.GetFromJsonAsync<RoomDetail>($"/api/rooms/{roomId}", Ct))!.Racks;
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Rumsvy" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        try
        {
            (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new
            {
                kind = "create_location",
                siteId,
                parentId = roomId,
                locationKind = "rack",
                name = "Rack 9",
                rackUnits = 47,
                roomRow = 2,
                roomSlot = 1,
                facing = "back",
            }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var moved = (await (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new
            {
                kind = "set_rack_place",
                type = "location",
                objectId = racks[0].Id,
                roomRow = 2,
                roomSlot = 2,
                facing = "back",
            }, Ct)).Content.ReadFromJsonAsync<PlanOperationView>(Ct))!;
            moved.Summary.ShouldBe($"Placera {racks[0].Name} i rad 2, plats 2, med fronten bakåt");

            // The place the second rack stands on is taken.
            var taken = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new
            {
                kind = "set_rack_place",
                type = "location",
                objectId = racks[0].Id,
                roomRow = racks[1].Row,
                roomSlot = racks[1].Slot,
            }, Ct);
            taken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await taken.Content.ReadAsStringAsync(Ct)).ShouldContain("tagen");
            (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new
            {
                kind = "create_location",
                siteId,
                parentId = roomId,
                locationKind = "room",
                name = "Fel",
                roomRow = 1,
                roomSlot = 1,
            }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            var inPlan = (await client.GetFromJsonAsync<RoomDetail>($"/api/rooms/{roomId}?plan={plan.Id}", Ct))!;
            var added = inPlan.Racks.Single(r => r.Plan == "new");
            (added.Id < 0, added.Name, added.Row, added.Slot, added.Facing, added.Units).ShouldBe((true, "Rack 9", 2, 1, "back", 47));
            var placed = inPlan.Racks.Single(r => r.Id == racks[0].Id);
            (placed.Row, placed.Slot, placed.Plan).ShouldBe((2, 2, "moved"));
            // Production is untouched.
            (await client.GetFromJsonAsync<RoomDetail>($"/api/rooms/{roomId}", Ct))!.Racks.ShouldBe(racks);
        }
        finally
        {
            await Exec(db, $"DELETE FROM plan_operation WHERE plan_id = {plan.Id}; DELETE FROM plan WHERE id = {plan.Id}");
        }
    }

    [Fact]
    public async Task Applying_a_plan_writes_the_new_rack_and_the_new_place()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(31, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        await Exec(db, "DELETE FROM plan_operation; DELETE FROM plan_dependency; DELETE FROM plan");
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);
        var (roomId, siteId) = await Pair(db, "SELECT m.id, m.site_id FROM location m WHERE m.kind = 'room' ORDER BY m.id LIMIT 1");
        var rack = await Scalar(db, $"SELECT id FROM location WHERE parent_id = {roomId} AND kind = 'rack' ORDER BY id LIMIT 1");

        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Nytt rack" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new
        {
            kind = "create_location",
            siteId,
            parentId = roomId,
            locationKind = "rack",
            name = "Rack 9",
            roomRow = 5,
            roomSlot = 1,
            facing = "front",
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new
        {
            kind = "set_rack_place",
            type = "location",
            objectId = rack,
            roomRow = 5,
            roomSlot = 2,
            facing = "back",
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Scalar(db, $"SELECT count(*) FROM location WHERE parent_id = {roomId} AND name = 'Rack 9' AND room_row = 5 AND room_slot = 1 AND facing = 'front'")).ShouldBe(1);
        (await Scalar(db, $"SELECT count(*) FROM location WHERE id = {rack} AND room_row = 5 AND room_slot = 2 AND facing = 'back'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_room_outside_the_scope_does_not_exist()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        var south = await Scalar(db, """
            SELECT m.id FROM location m JOIN site s ON s.id = m.site_id
            WHERE m.kind = 'room' AND ST_Y(ST_PointOnSurface(s.geom)) < 6400000 ORDER BY m.id LIMIT 1
            """);
        using var north = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await north.GetAsync($"/api/rooms/{south}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var full = NetworkFixture.Client(api);
        (await full.GetAsync($"/api/rooms/{south}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(long, long)> Pair(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}

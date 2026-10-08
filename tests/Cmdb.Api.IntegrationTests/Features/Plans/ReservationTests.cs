using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Cables;
using Cmdb.Api.Features.Equipment;
using Cmdb.Api.Features.Plans;
using Cmdb.Api.Features.Reservations;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Plans;

/// <summary>
/// Reservations and conflicts (#25): two plans wanting the same fibre, a reservation that blocks the other plan, and
/// claims visible on ports and conductors. Each test has its own network.
/// </summary>
public sealed class ReservationTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_plans_wanting_the_same_fibre_see_each_other_and_a_reservation_decides()
    {
        var (db, api) = await NetworkAsync(21);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var ports = await FreePortsAsync(db, 2);
        var (end, conductor, cable) = await FreeConductorEndAsync(db);

        var a = await CreateAsync(client, "Förbindelse A");
        var opA = await AddAsync(client, a.Id, new { kind = "connect", a = ports[0], b = end, connectionKind = "splice" });
        opA.Conflicts.ShouldBeEmpty();
        var b = await CreateAsync(client, "Förbindelse B");
        var opB = await AddAsync(client, b.Id, new { kind = "connect", a = ports[1], b = end, connectionKind = "splice" });
        opB.Conflicts.ShouldBe(["Önskas också av planen Förbindelse A."]);
        opB.Blocked.ShouldBeFalse();

        var plans = (await client.GetFromJsonAsync<List<PlanSummary>>("/api/plans", Ct))!;
        plans.Single(p => p.Id == a.Id).Conflicts.ShouldBe(1);
        plans.Single(p => p.Id == b.Id).Conflicts.ShouldBe(1);
        var detail = (await client.GetFromJsonAsync<CableDetail>($"/api/cables/{cable}", Ct))!;
        detail.Claims!.Single(c => c.ConductorId == conductor).Claims.WantedBy.Select(p => p.Name).ShouldBe(["Förbindelse A", "Förbindelse B"]);
        detail.Claims!.Single(c => c.ConductorId == conductor).Claims.Conflict.ShouldBeTrue();

        var number = await Scalar(db, $"SELECT number FROM conductor WHERE id = {conductor}");
        (await client.GetFromJsonAsync<ConductorId>($"/api/cables/{cable}/conductors/{number}", Ct))!.Id.ShouldBe(conductor);
        (await client.GetAsync($"/api/cables/{cable}/conductors/9999", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A reserves the fibre: B is now blocked, A only warned.
        var reservation = await ReserveAsync(client, new { resourceKind = "conductor", resourceId = conductor, planId = a.Id, reason = "Kundorder 17" });
        reservation.Label.ShouldContain("ledare");
        reservation.Holder.ShouldBe("Förbindelse A");
        var bView = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{b.Id}/view", Ct))!;
        bView.Changes.Single().Conflicts.ShouldBe(["Reserverad av planen Förbindelse A."]);
        bView.Changes.Single().Blocked.ShouldBeTrue();
        (await client.PostAsync($"/api/plans/{b.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var second = await client.PostAsJsonAsync("/api/reservations", new { resourceKind = "conductor", resourceId = conductor, planId = b.Id }, Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync(Ct)).ShouldContain("Förbindelse A");

        // A goes into production: its reservation is released and B's splice no longer fits.
        (await client.PostAsync($"/api/plans/{a.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<List<ReservationView>>($"/api/reservations?plan={a.Id}", Ct))!.ShouldBeEmpty();
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);
        bView = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{b.Id}/view", Ct))!;
        bView.Changes.Single().Problem.ShouldBe("Porten eller fibern är redan upptagen av en koppling av samma slag.");
        bView.Changes.Single().Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plans_building_on_each_other_do_not_conflict()
    {
        var (db, api) = await NetworkAsync(22);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var ports = await FreePortsAsync(db, 2);

        var first = await CreateAsync(client, "Etapp 1");
        await AddAsync(client, first.Id, new { kind = "connect", a = ports[0], b = ports[1], connectionKind = "patch" });
        await ReserveAsync(client, new { resourceKind = "terminal", resourceId = ports[0], planId = first.Id });
        var second = await CreateAsync(client, "Etapp 2", first.Id);
        // Undo and redo in the later stage: same resources, same chain.
        await AddAsync(client, second.Id, new { kind = "disconnect", a = ports[0], b = ports[1] });
        var redo = await AddAsync(client, second.Id, new { kind = "connect", a = ports[0], b = ports[1], connectionKind = "patch" });

        redo.Conflicts.ShouldBeEmpty();
        redo.Problem.ShouldBeNull();
        var equipment = await Scalar(db, $"SELECT equipment_id FROM port WHERE terminal_id = {ports[0]}");
        var port = (await client.GetFromJsonAsync<EquipmentDetail>($"/api/equipment/{equipment}", Ct))!.Ports.Single(p => p.TerminalId == ports[0]);
        port.Claims!.Reservation!.Holder.ShouldBe("Etapp 1");
        port.Claims.WantedBy.Select(p => p.Name).ShouldBe(["Etapp 1", "Etapp 2"]);
        port.Claims.Conflict.ShouldBeFalse("the plans build on each other and the holder is one of them");
    }

    [Fact]
    public async Task Reservations_are_checked_released_and_limited_by_scope()
    {
        var (db, api) = await NetworkAsync(23);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        using var region = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var ports = await FreePortsAsync(db, 1);
        var service = await Scalar(db, "SELECT min(id) FROM service");
        var equipment = await Scalar(db, """
            SELECT e.id FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id
            WHERE jsonb_array_length(t.slot_template) > 0 ORDER BY e.id LIMIT 1
            """);
        var slot = await Text(db, $"SELECT t.slot_template->0->>'name' FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id WHERE e.id = {equipment}");

        var byService = await ReserveAsync(client, new { resourceKind = "terminal", resourceId = ports[0], serviceId = service, reason = "Framtida uppgradering" });
        byService.HolderKind.ShouldBe("service");
        (await client.GetFromJsonAsync<List<ReservationView>>($"/api/reservations?service={service}", Ct))!.Single().Id.ShouldBe(byService.Id);
        var slotReservation = await ReserveAsync(client, new { resourceKind = "slot", resourceId = equipment, slot, serviceId = service });
        slotReservation.Label.ShouldContain($"slot {slot}");

        // Unknown resources, a slot the equipment does not have, two holders, and a region user who may not write.
        (await client.PostAsJsonAsync("/api/reservations", new { resourceKind = "terminal", resourceId = 999_999_999L, serviceId = service }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/reservations", new { resourceKind = "slot", resourceId = equipment, slot = "no-such-slot", serviceId = service }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/reservations", new { resourceKind = "terminal", resourceId = ports[0], serviceId = service, planId = 1L }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await region.PostAsJsonAsync("/api/reservations", new { resourceKind = "terminal", resourceId = ports[0], serviceId = service }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.DeleteAsync($"/api/reservations/{byService.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<List<ReservationView>>($"/api/reservations?service={service}", Ct))!.Select(r => r.Id).ShouldBe([slotReservation.Id]);
        // Released: someone else can take it now.
        var plan = await CreateAsync(client, "Tar porten");
        await ReserveAsync(client, new { resourceKind = "terminal", resourceId = ports[0], planId = plan.Id });
        (await client.PostAsync($"/api/plans/{plan.Id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<List<ReservationView>>($"/api/reservations?plan={plan.Id}", Ct))!.ShouldBeEmpty();
    }

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> NetworkAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        // The generator's demo plans claim free ports too; these tests start without plans.
        await using (var clear = db.CreateCommand("DELETE FROM plan_operation; DELETE FROM plan_dependency; DELETE FROM plan"))
        {
            await clear.ExecuteNonQueryAsync(Ct);
        }
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (db, api);
    }

    private static async Task<PlanSummary> CreateAsync(HttpClient client, string name, params long[] dependsOn)
    {
        var response = await client.PostAsJsonAsync("/api/plans", new { name, dependsOn }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
    }

    private static async Task<PlanOperationView> AddAsync(HttpClient client, long plan, object operation)
    {
        var response = await client.PostAsJsonAsync($"/api/plans/{plan}/operations", operation, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanOperationView>(Ct))!;
    }

    private static async Task<ReservationView> ReserveAsync(HttpClient client, object reservation)
    {
        var response = await client.PostAsJsonAsync("/api/reservations", reservation, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ReservationView>(Ct))!;
    }

    /// <summary>Unconnected ports in id order.</summary>
    private static async Task<List<long>> FreePortsAsync(NpgsqlDataSource db, int count)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT p.terminal_id FROM port p
            WHERE NOT EXISTS (SELECT 1 FROM connection c WHERE c.a_terminal_id = p.terminal_id OR c.b_terminal_id = p.terminal_id)
            ORDER BY p.terminal_id LIMIT {count}
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>A conductor end without a splice, its conductor and its cable.</summary>
    private static async Task<(long End, long Conductor, long Cable)> FreeConductorEndAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("""
            SELECT ce.terminal_id, ce.conductor_id, k.cable_id FROM conductor_end ce JOIN conductor k ON k.id = ce.conductor_id
            WHERE NOT EXISTS (SELECT 1 FROM connection c WHERE ce.terminal_id IN (c.a_terminal_id, c.b_terminal_id))
            ORDER BY ce.terminal_id LIMIT 1
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> Text(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

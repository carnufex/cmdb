using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Cables;
using Cmdb.Api.Features.Circuits;
using Cmdb.Api.Features.Equipment;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Services;
using Cmdb.Api.Features.Sites;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Objects;

public sealed class ObjectDetailTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Site_shows_locations_with_equipment_and_cables_to_neighbours()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        var id = await Scalar(db, "SELECT id FROM site WHERE site_type = 'radio' ORDER BY id LIMIT 1");
        using var client = NetworkFixture.Client(api);

        var site = (await client.GetFromJsonAsync<SiteDetail>($"/api/sites/{id}", Ct))!;

        site.Code.ShouldStartWith("RAD-");
        site.Locations.Select(l => l.Kind).ShouldBe(["building", "room", "rack"], ignoreOrder: true);
        site.Locations.SelectMany(l => l.Equipment).ShouldContain(e => e.Model == "BB-6");
        site.Cables.ShouldNotBeEmpty();
        site.Cables.ShouldAllBe(c => c.OtherEnd.Id != id);
        var impact = (await client.GetFromJsonAsync<Impact>($"/api/sites/{id}/impact", Ct))!;
        impact.Services.ShouldContain(s => s.Service.Name == $"Mobil backhaul {site.Code}");
    }

    [Fact]
    public async Task Equipment_ports_show_what_they_connect_to_as_links()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        var id = await Scalar(db, "SELECT e.id FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id WHERE t.key = 'acme-bb-6' ORDER BY e.id LIMIT 1");
        using var client = NetworkFixture.Client(api);

        var equipment = (await client.GetFromJsonAsync<EquipmentDetail>($"/api/equipment/{id}", Ct))!;

        equipment.Model.ShouldBe("BB-6");
        equipment.LocationPath.ShouldBe("Teknikbod / Utrustningsrum / Rack 1");
        equipment.Ports.Count.ShouldBe(9);
        var backhaul = equipment.Ports.Single(p => p.Name == "bh1");
        backhaul.Circuits.ShouldBeGreaterThan(0);
        var peer = backhaul.Connections.ShouldHaveSingleItem().Peer;
        peer.Owner.Type.ShouldBe("equipment");
        peer.Label.ShouldStartWith("Port ");
        equipment.Ports.Select(p => (p.Row, p.Column)).Distinct().Count().ShouldBe(9);
    }

    [Fact]
    public async Task Chassis_lists_cards_by_slot_and_free_slots()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        var id = await Scalar(db, "SELECT e.id FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id WHERE t.key = 'acme-cr-8' ORDER BY e.id LIMIT 1");
        using var client = NetworkFixture.Client(api);

        var chassis = (await client.GetFromJsonAsync<EquipmentDetail>($"/api/equipment/{id}", Ct))!;

        chassis.Cards.ShouldContain(c => c.Slot == "8");
        chassis.FreeSlots.ShouldNotContain("8");
        (chassis.Cards.Count + chassis.FreeSlots.Count).ShouldBe(8);
    }

    [Fact]
    public async Task Cable_shows_its_circuits_and_impact_lists_the_services_that_depend_on_it()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        var id = await Scalar(db, """
            SELECT c.id FROM cable c JOIN site s ON s.id = c.a_site_id
            WHERE s.site_type = 'radio' AND c.lifecycle = 'in_service' ORDER BY c.id LIMIT 1
            """);
        using var client = NetworkFixture.Client(api);

        var cable = (await client.GetFromJsonAsync<CableDetail>($"/api/cables/{id}", Ct))!;

        cable.A.Code.ShouldStartWith("RAD-");
        cable.ConductorsInUse.ShouldBeGreaterThan(0);
        cable.Circuits.ShouldNotBeEmpty();
        cable.Circuits.ShouldAllBe(c => c.Name == "physical");
        var impact = (await client.GetFromJsonAsync<Impact>($"/api/cables/{id}/impact", Ct))!;
        impact.Circuits.ShouldBeGreaterThan(cable.Circuits.Count);
        impact.Services.ShouldContain(s => s.Service.Name!.StartsWith("Mobil backhaul", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Service_and_circuit_link_through_the_layers()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        var id = await Scalar(db, "SELECT id FROM service WHERE service_type = 'mobile-backhaul' ORDER BY id LIMIT 1");
        using var client = NetworkFixture.Client(api);

        var service = (await client.GetFromJsonAsync<ServiceDetail>($"/api/services/{id}", Ct))!;
        var logical = service.Circuits.ShouldHaveSingleItem();
        logical.A!.Label.ShouldBe("Port bh1");
        var circuit = (await client.GetFromJsonAsync<CircuitDetail>($"/api/circuits/{logical.Circuit.Id}", Ct))!;

        circuit.Layer.ShouldBe("logical");
        circuit.Hops[0].Channel.ShouldStartWith("vlan ");
        circuit.Carriers.Count.ShouldBe(2);
        circuit.Services.ShouldContain(s => s.Id == id);
        var carrier = (await client.GetFromJsonAsync<CircuitDetail>($"/api/circuits/{circuit.Carriers[0].Id}", Ct))!;
        carrier.Layer.ShouldBe("physical");
        carrier.Carried.ShouldContain(c => c.Id == circuit.Id);
        carrier.Hops.ShouldContain(h => h.Terminal.Owner.Type == "cable");
    }

    [Theory]
    [InlineData("site")]
    [InlineData("equipment")]
    [InlineData("cable")]
    [InlineData("service")]
    [InlineData("circuit")]
    public async Task Summaries_give_code_status_and_facts(string type)
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        var id = await Scalar(db, $"SELECT min(id) FROM {type}");
        using var client = NetworkFixture.Client(api);

        var summary = (await client.GetFromJsonAsync<ObjectSummary>($"/api/summary/{type}/{id}", Ct))!;

        summary.Code.ShouldNotBeNullOrEmpty();
        summary.Lifecycle.ShouldNotBeNullOrEmpty();
        summary.Facts.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Unknown_objects_are_not_found()
    {
        var (_, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);

        foreach (var path in new[] { "/api/sites/999999", "/api/equipment/999999", "/api/cables/999999", "/api/services/999999", "/api/circuits/999999", "/api/summary/site/999999", "/api/summary/toaster/1" })
        {
            (await client.GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }
    }

    [Fact]
    public async Task Sites_and_equipment_are_edited_in_place_by_fully_authorised_users_only()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        var id = await Scalar(db, "SELECT max(id) FROM site WHERE site_type = 'cabinet'");
        using var full = NetworkFixture.Client(api);
        using var region = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);

        (await region.PatchAsJsonAsync($"/api/sites/{id}", new { name = "Nope" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await full.PatchAsJsonAsync($"/api/sites/{id}", new { lifecycle = "sometimes" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await full.PatchAsJsonAsync($"/api/sites/{id}", new { name = "Omdöpt skåp", lifecycle = "decommissioning" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var site = (await full.GetFromJsonAsync<SiteDetail>($"/api/sites/{id}", Ct))!;
        site.Name.ShouldBe("Omdöpt skåp");
        site.Lifecycle.ShouldBe("decommissioning");
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

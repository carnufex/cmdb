using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Equipment;
using Cmdb.Catalog;
using Cmdb.Database;

namespace Cmdb.Api.IntegrationTests.Features.Equipment;

public sealed class CreateEquipmentTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creates_equipment_with_ports_from_the_type_template()
    {
        var rack = await NewRackAsync();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/equipment", new
        {
            typeKey = "acme-ax-48",
            name = "SW-1",
            locationId = rack,
            attributes = new { serialNumber = "SN-0001" },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<CreateEquipmentResponse>(Ct))!;
        created.Ports.Count.ShouldBe(52);
        created.Ports[0].Name.ShouldBe("ge-0/0/1");
        created.Ports[^1].Name.ShouldBe("xe-0/1/4");
        created.Ports.Select(p => p.TerminalId).Distinct().Count().ShouldBe(52);

        (await Scalar<long>($"SELECT count(*) FROM port p JOIN terminal t ON t.id = p.terminal_id AND t.kind = 'port' WHERE p.equipment_id = {created.Id}"))
            .ShouldBe(52);
    }

    [Fact]
    public async Task Cards_go_in_slots_that_accept_them_and_name_ports_after_the_slot()
    {
        var chassis = await CreateAsync(new { typeKey = "acme-cr-8", name = "CR-1", locationId = await NewRackAsync() });
        using var client = factory.CreateAuthenticatedClient();

        var card = await client.PostAsJsonAsync("/api/equipment", new { typeKey = "acme-lc-24x", name = "LC-3", parentId = chassis.Id, slot = "3" }, Ct);
        var again = await client.PostAsJsonAsync("/api/equipment", new { typeKey = "acme-lc-4c", name = "LC-3b", parentId = chassis.Id, slot = "3" }, Ct);
        var noSuchSlot = await client.PostAsJsonAsync("/api/equipment", new { typeKey = "acme-lc-4c", name = "LC-9", parentId = chassis.Id, slot = "9" }, Ct);
        var notACard = await client.PostAsJsonAsync("/api/equipment", new { typeKey = "acme-ax-24", name = "SW", parentId = chassis.Id, slot = "4" }, Ct);

        card.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await card.Content.ReadFromJsonAsync<CreateEquipmentResponse>(Ct))!.Ports[0].Name.ShouldBe("xe-3/0/1");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        noSuchSlot.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        notACard.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("""{ "typeKey": "acme-nope", "name": "X", "locationId": 1 }""")]
    [InlineData("""{ "typeKey": "acme-ant-4p", "name": "X", "locationId": 1, "attributes": { "azimuthDeg": 999 } }""")]
    [InlineData("""{ "typeKey": "acme-ax-24", "name": "X" }""")]
    [InlineData("""{ "typeKey": "acme-ax-24", "name": "X", "locationId": 1, "parentId": 1, "slot": "1" }""")]
    [InlineData("""{ "typeKey": "acme-ax-24", "name": "X", "locationId": 999999 }""")]
    public async Task Rejects_invalid_requests(string body)
    {
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/equipment", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_fully_authorised_users_may_write_until_access_scopes_exist()
    {
        using var client = factory.CreateAuthenticatedClient("cmdb-demo-region", ["cmdb-region-nord"]);

        var response = await client.PostAsJsonAsync("/api/equipment", new { typeKey = "acme-ax-24", name = "X", locationId = 1 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Catalog_sync_is_idempotent()
    {
        await using var context = CmdbDatabase.CreateContext(factory.Db);
        (await CatalogSync.SyncAsync(context, TypeCatalog.Embedded, Ct)).ShouldBe(0);
        (await Scalar<long>("SELECT count(*) FROM equipment_type WHERE key LIKE 'acme-%'")).ShouldBe(TypeCatalog.Embedded.Types.Count);
    }

    private async Task<CreateEquipmentResponse> CreateAsync(object body)
    {
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/equipment", body, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateEquipmentResponse>(Ct))!;
    }

    private async Task<long> NewRackAsync() => await Scalar<long>($"""
        WITH s AS (INSERT INTO site (code, name, site_type, geom) VALUES ('T-{Guid.NewGuid():N}', 'Test', 'core', 'SRID=3006;POINT(500000 6500000)') RETURNING id)
        INSERT INTO location (site_id, kind, name) SELECT id, 'rack', 'R1' FROM s RETURNING id
        """);

    private async Task<T> Scalar<T>(string sql)
    {
        await using var cmd = factory.Db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

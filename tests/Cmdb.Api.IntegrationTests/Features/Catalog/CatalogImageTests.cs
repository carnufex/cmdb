using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cmdb.Api.Features.Equipment;
using Cmdb.Catalog;

namespace Cmdb.Api.IntegrationTests.Features.Catalog;

/// <summary>Panel images (#214): served from the catalog behind sign-in, with the ports placed on them.</summary>
public sealed class CatalogImageTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Equipment_carries_its_panel_images_and_port_areas()
    {
        using var client = factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/equipment", new { typeKey = "acme-rect-48", name = "LR-1", locationId = await NewRackAsync() }, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<CreateEquipmentResponse>(Ct))!.Id;

        var detail = (await client.GetFromJsonAsync<EquipmentDetail>($"/api/equipment/{id}", Ct))!;

        var images = detail.Panel.GetProperty("images");
        images.GetProperty("front").GetProperty("file").GetString().ShouldBe("acme-rect-48-front.svg");
        images.GetProperty("back").GetProperty("width").GetInt32().ShouldBe(960);
        detail.Ports.Single(p => p.Name == "ac").Box.ShouldBe(new PortBox("back", 40, 60, 60, 50));
        detail.Ports.Single(p => p.Name == "mon").Box!.Side.ShouldBe("front");
    }

    [Fact]
    public async Task An_image_is_served_with_a_policy_that_runs_no_script_and_revalidates()
    {
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/catalog/images/acme-odf-96-front.svg", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(CatalogSource.Embedded.Image("acme-odf-96-front.svg"));
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");

        using var again = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/images/acme-odf-96-front.svg");
        again.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        (await client.SendAsync(again, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Theory]
    [InlineData("nope.svg")]
    [InlineData("..%2Fcable-types.json")]
    [InlineData("cable-types.json")]
    public async Task Only_catalog_images_are_served(string file)
    {
        using var client = factory.CreateAuthenticatedClient();

        (await client.GetAsync($"/api/catalog/images/{file}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Images_need_sign_in()
    {
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/catalog/images/acme-odf-96-front.svg", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<long> NewRackAsync()
    {
        await using var cmd = factory.Db.CreateCommand($"""
            WITH s AS (INSERT INTO site (code, name, site_type, geom) VALUES ('T-{Guid.NewGuid():N}', 'Test', 'core', 'SRID=3006;POINT(500000 6500000)') RETURNING id)
            INSERT INTO location (site_id, kind, name) SELECT id, 'rack', 'R1' FROM s RETURNING id
            """);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

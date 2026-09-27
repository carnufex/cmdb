using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Search;

namespace Cmdb.Api.IntegrationTests.Features.Search;

public sealed class SearchTests(ApiFactory factory) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var cmd = factory.Db.CreateCommand("""
            INSERT INTO site (code, name, site_type, geom, lifecycle) VALUES
                ('SRCH-1', 'Sökbar nod', 'hub', 'SRID=3006;POINT(600000 6600000)', 'in_service'),
                ('SRCH-10', 'Sökbar nod tio', 'radio', 'SRID=3006;POINT(700000 7200000)', 'planned'),
                ('XSRCH-2', 'Annan sökbar', 'cabinet', 'SRID=3006;POINT(601000 6601000)', 'in_service'),
                ('NEAR-A', 'Närhet', 'cabinet', 'SRID=3006;POINT(400000 6200000)', 'in_service'),
                ('NEAR-B', 'Närhet', 'cabinet', 'SRID=3006;POINT(800000 7600000)', 'in_service')
            ON CONFLICT (code) DO NOTHING;
            INSERT INTO location (site_id, kind, name)
            SELECT id, 'rack', 'R1' FROM site WHERE code = 'SRCH-1'
            ON CONFLICT DO NOTHING;
            INSERT INTO equipment (equipment_type_id, site_id, location_id, name, attributes, lifecycle)
            SELECT et.id, l.site_id, l.id, 'SRCH-1 AX-24 1', '{"serialNumber": "SNSEARCHME42"}', 'in_service'
            FROM equipment_type et, location l JOIN site s ON s.id = l.site_id
            WHERE et.key = 'acme-ax-24' AND s.code = 'SRCH-1'
              AND NOT EXISTS (SELECT 1 FROM equipment WHERE name = 'SRCH-1 AX-24 1');
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Exact_code_first_then_prefix_then_contains()
    {
        var hits = await SearchAsync("srch-1");

        hits[0].Code.ShouldBe("SRCH-1");
        hits[0].Type.ShouldBe("site");
        hits[0].X.ShouldBe(600000);
        var codes = hits.Select(h => h.Code).ToList();
        codes.IndexOf("SRCH-10").ShouldBeLessThan(codes.IndexOf("XSRCH-2") is -1 ? int.MaxValue : codes.IndexOf("XSRCH-2"));
    }

    [Fact]
    public async Task Sites_rank_before_equipment_at_the_same_match_level()
    {
        var hits = await SearchAsync("SRCH-1");

        var site = hits.FindIndex(h => h.Type == "site" && h.Code == "SRCH-1");
        var equipment = hits.FindIndex(h => h.Type == "equipment" && h.Code == "SRCH-1 AX-24 1");
        site.ShouldBeGreaterThanOrEqualTo(0);
        equipment.ShouldBeGreaterThan(site);
    }

    [Fact]
    public async Task Finds_equipment_by_attribute_values()
    {
        var hits = await SearchAsync("searchme42");

        hits.ShouldContain(h => h.Type == "equipment" && h.Code == "SRCH-1 AX-24 1" && h.Detail == "Acme Networks AX-24");
    }

    [Fact]
    public async Task Nearer_objects_rank_higher_when_near_is_given()
    {
        (await SearchAsync("Närhet", near: "400100,6200100"))[0].Code.ShouldBe("NEAR-A");
        (await SearchAsync("Närhet", near: "799900,7599900"))[0].Code.ShouldBe("NEAR-B");
    }

    [Fact]
    public async Task Like_wildcards_in_the_query_are_literal()
    {
        (await SearchAsync("%_%")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/api/search?q=ab")]
    [InlineData("/api/search?q=abc&near=nope")]
    [InlineData("/api/search?q=abc&limit=500")]
    public async Task Rejects_invalid_queries(string path)
    {
        using var client = factory.CreateAuthenticatedClient();

        (await client.GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Requires_a_token()
    {
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/search?q=abc", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<List<SearchHit>> SearchAsync(string q, string? near = null)
    {
        using var client = factory.CreateAuthenticatedClient();
        var url = $"/api/search?q={Uri.EscapeDataString(q)}" + (near is null ? "" : $"&near={near}");
        return (await client.GetFromJsonAsync<List<SearchHit>>(url, Ct))!;
    }
}

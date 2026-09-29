using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Api.Features.Query;
using Cmdb.Api.Features.Search;
using Cmdb.Api.Features.Sites;
using Cmdb.Api.Features.Trace;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Auth;

/// <summary>
/// Access scopes at the edges (#22, step 3): clipping cables at a scope's border, hidden positions, and the MCP tools
/// for each demo user. Uses the shared small network with two extra test scopes over the same northern area.
/// </summary>
public sealed class ScopeEdgeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string North = "ST_Y(ST_PointOnSurface(s.geom)) > 6950000";
    private const string NorthArea = "ST_GeomFromText('POLYGON((200000 6950000, 1000000 6950000, 1000000 7800000, 200000 7800000, 200000 6950000))', 3006)";

    // Tile grid (TileGrid): one z0 square over x -1 200 000..1 800 000, y 5 500 000..8 500 000.
    private const double GridMinX = -1_200_000, GridMaxY = 8_500_000, GridSize = 3_000_000;

    [Fact]
    public async Task A_clipping_scope_cuts_cables_at_its_edge()
    {
        var (db, api) = await WithTestScopesAsync();
        using var whole = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        using var clip = NetworkFixture.Client(api, "cmdb-test-klipp", ["cmdb-test-klipp"]);
        // A cable across the edge whose southern end is well away from it, and the z8 tile around that end.
        var (cable, x, y) = await CrossingCableAsync(db);
        const int z = 8;
        var size = GridSize / (1 << z);
        var (tx, ty) = ((int)((x - GridMinX) / size), (int)((GridMaxY - y) / size));
        (GridMaxY - (ty * size)).ShouldBeLessThan(6_950_000, "the tile lies south of the edge");

        // Whole: the southern part of the cable is drawn; clip: nothing south of the edge.
        (await TileAsync(whole, z, tx, ty)).Length.ShouldBeGreaterThan(0);
        (await TileAsync(clip, z, tx, ty)).Length.ShouldBe(0);

        // Both see the cable itself, with the far end as a placeholder.
        (await clip.GetAsync($"/api/cables/{cable}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await whole.GetAsync($"/api/cables/{cable}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_clipping_scope_shows_cables_passing_through_its_area()
    {
        var (db, _) = await WithTestScopesAsync();
        var through = await Scalar(db, $"""
            SELECT count(*) FROM cable c JOIN site a ON a.id = c.a_site_id JOIN site b ON b.id = c.b_site_id
            WHERE ST_Y(ST_PointOnSurface(a.geom)) <= 6950000 AND ST_Y(ST_PointOnSurface(b.geom)) <= 6950000
              AND ST_Intersects(c.geom, {NorthArea})
            """);
        var clipped = await Scalar(db, "SELECT count(*) FROM scope_cable WHERE scope_key = 'test-klipp'");
        var whole = await Scalar(db, "SELECT count(*) FROM scope_cable WHERE scope_key = 'region-nord'");

        clipped.ShouldBe(whole + through);
    }

    [Fact]
    public async Task Hidden_coordinates_leave_no_position_anywhere()
    {
        var (db, api) = await WithTestScopesAsync();
        using var client = NetworkFixture.Client(api, "cmdb-test-pos", ["cmdb-test-dolda-positioner"]);
        var site = await Scalar(db, $"SELECT s.id FROM site s WHERE {North} AND s.site_type = 'aggregation' ORDER BY s.id LIMIT 1");
        var code = await Text(db, $"SELECT code FROM site WHERE id = {site}");
        var service = await Scalar(db, "SELECT service_id FROM scope_service WHERE scope_key = 'test-dolda-positioner' ORDER BY service_id LIMIT 1");

        var detail = (await client.GetFromJsonAsync<SiteDetail>($"/api/sites/{site}", Ct))!;
        detail.X.ShouldBeNull();
        detail.Y.ShouldBeNull();
        var hits = (await client.GetFromJsonAsync<List<SearchHit>>($"/api/search?q={code}&near=500000,7000000", Ct))!;
        hits.ShouldContain(h => h.Code == code);
        hits.ShouldAllBe(h => h.X == null && h.Y == null);
        var query = (await (await client.PostAsJsonAsync("/api/query/sites", new { limit = 10 }, Ct)).Content.ReadFromJsonAsync<SiteQueryResult>(Ct))!;
        query.Total.ShouldBeGreaterThan(0);
        query.Points.ShouldBeEmpty();
        query.Extent.ShouldBeNull();
        query.Sites.ShouldAllBe(s => s.X == null && s.Y == null);
        (await client.GetFromJsonAsync<SiteGraph>($"/api/sites/{site}/graph", Ct))!.Nodes.ShouldAllBe(n => n.X == null && n.Y == null);
        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?service={service}&geometry=true", Ct))!;
        trace.Route.ShouldNotBeNull();
        trace.Route.Sites.ShouldBeEmpty();
        trace.Route.Cables.ShouldBeEmpty();
        for (var z = 0; z <= 6; z += 3)
        {
            var size = GridSize / (1 << z);
            (await TileAsync(client, z, (int)((600_000 - GridMinX) / size), (int)((GridMaxY - 7_000_000) / size))).Length.ShouldBe(0);
        }

        // The same user without the hiding scope sees positions, so the difference is the scope.
        using var region = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await region.GetFromJsonAsync<SiteDetail>($"/api/sites/{site}", Ct))!.X.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("cmdb-demo-region", "cmdb-region-nord")]
    [InlineData("cmdb-demo-projekt", "cmdb-projekt-a")]
    [InlineData("cmdb-nobody", null)]
    public async Task Mcp_tools_keep_each_demo_user_inside_their_scope(string user, string? group)
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        string[] groups = group is null ? [] : [group];
        var scopeKey = group switch { "cmdb-region-nord" => "region-nord", "cmdb-projekt-a" => "projekt-a", _ => "none" };
        // A hub outside every restricted scope, a service it carries, and a cable at it.
        var outside = await Scalar(db, """
            SELECT s.id FROM site s WHERE s.site_type = 'hub'
              AND NOT EXISTS (SELECT 1 FROM scope_site z WHERE z.site_id = s.id AND z.scope_key IN ('region-nord', 'projekt-a'))
            ORDER BY s.id LIMIT 1
            """);
        var outsideCode = await Text(db, $"SELECT code FROM site WHERE id = {outside}");
        var cable = await Scalar(db, $"""
            SELECT c.id FROM cable c WHERE (c.a_site_id = {outside} OR c.b_site_id = {outside})
              AND NOT EXISTS (SELECT 1 FROM scope_cable z WHERE z.cable_id = c.id AND z.scope_key IN ('region-nord', 'projekt-a'))
            ORDER BY c.id LIMIT 1
            """);
        var service = await Scalar(db, """
            SELECT s.id FROM service s
            WHERE NOT EXISTS (SELECT 1 FROM scope_service z WHERE z.service_id = s.id AND z.scope_key IN ('region-nord', 'projekt-a'))
            ORDER BY s.id LIMIT 1
            """);
        var visible = await Scalar(db, $"SELECT count(*) FROM scope_site WHERE scope_key = '{scopeKey}'");

        await using var mcp = await ConnectAsync(api, user, groups);

        foreach (var reference in new[] { $"site:{outside}", outsideCode, $"cable:{cable}", $"service:{service}" })
        {
            var result = await mcp.CallToolAsync("get_object", new Dictionary<string, object?> { ["reference"] = reference }, cancellationToken: Ct);
            result.IsError.ShouldBe(true, reference);
        }
        (await mcp.CallToolAsync("trace", new Dictionary<string, object?> { ["reference"] = $"service:{service}" }, cancellationToken: Ct))
            .IsError.ShouldBe(true);
        // Impact outside the scope answers like an object that affects nothing: no counts, nothing hidden.
        var impact = Json(await mcp.CallToolAsync("impact", new Dictionary<string, object?> { ["reference"] = $"cable:{cable}" }, cancellationToken: Ct));
        impact.GetRawText().ShouldNotContain(outsideCode);
        foreach (var property in impact.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Name != "elapsedMs"))
        {
            property.Value.GetDouble().ShouldBe(0, property.Name);
        }

        var search = Json(await mcp.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = outsideCode }, cancellationToken: Ct));
        search.GetProperty("hits").EnumerateArray().ShouldNotContain(h => h.GetProperty("code").GetString() == outsideCode);
        var found = Json(await mcp.CallToolAsync("find_sites", new Dictionary<string, object?> { ["limit"] = 5 }, cancellationToken: Ct));
        using var rest = NetworkFixture.Client(api, user, groups);
        var restTotal = (await (await rest.PostAsJsonAsync("/api/query/sites", new { limit = 5 }, Ct)).Content.ReadFromJsonAsync<SiteQueryResult>(Ct))!.Total;
        found.GetProperty("total").GetInt64().ShouldBe(restTotal);
        restTotal.ShouldBeLessThanOrEqualTo(visible);
        if (group is null)
        {
            restTotal.ShouldBe(0);
        }
    }

    /// <summary>
    /// Two test scopes over region Nord's area: one clipping cables at its edge, one hiding positions. Idempotent, since
    /// the network is shared.
    /// </summary>
    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> WithTestScopesAsync()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        await using (var cmd = db.CreateCommand($$"""
            INSERT INTO access_scope (key, name, area, site_types, hidden_attributes, plans, crossing_mode, groups, db_roles, reason, granted_by, approved_by)
            VALUES ('test-klipp', 'Test klipp', {{NorthArea}}, '{}', '{}', '{}', 'clip', '{cmdb-test-klipp}', '{}', 'test', 'a', 'b'),
                   ('test-dolda-positioner', 'Test dolda positioner', {{NorthArea}}, '{}', '{coordinates}', '{}', 'whole', '{cmdb-test-dolda-positioner}', '{}', 'test', 'a', 'b')
            ON CONFLICT (key) DO NOTHING
            """))
        {
            if (await cmd.ExecuteNonQueryAsync(Ct) > 0)
            {
                await Cmdb.Database.Scopes.ScopeVisibility.RefreshAsync(db, Ct);
            }
        }
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRegistry>().LoadAsync(Ct);
        return (db, api);
    }

    private static async Task<(long Cable, double X, double Y)> CrossingCableAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.id, ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom))
            FROM cable c JOIN site n ON n.id IN (c.a_site_id, c.b_site_id) JOIN site s ON s.id IN (c.a_site_id, c.b_site_id)
            WHERE ST_Y(ST_PointOnSurface(n.geom)) > 6950000 AND ST_Y(ST_PointOnSurface(s.geom)) < 6950000 - 30000
            ORDER BY c.id LIMIT 1
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue("the small network has a cable across the region edge");
        return (reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2));
    }

    private static async Task<byte[]> TileAsync(HttpClient client, int z, int x, int y)
    {
        var response = await client.GetAsync($"/api/tiles/{z}/{x}/{y}", Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> api, string user, string[] groups)
    {
        var http = NetworkFixture.Client(api, user, groups);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static JsonElement Json(CallToolResult result)
    {
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        result.IsError.ShouldNotBe(true, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct) ?? throw new InvalidOperationException($"No row for: {sql}"),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> Text(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

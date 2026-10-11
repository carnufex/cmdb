using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Api.Features.Search;
using Cmdb.Api.Features.ServicePoints;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.ServicePoints;

/// <summary>
/// Service points (#292, ADR-0021): created, moved, connected and imported in a plan, written when it is applied, and read
/// through the panel's endpoint, the list, search, map tiles and the agents' tools, all within the caller's scopes.
/// </summary>
public sealed class ServicePointTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Service_points_are_planned_imported_connected_and_applied()
    {
        var (db, api) = await ApiAsync(62);
        await using var _ = api;
        using var client = NetworkFixture.Client(api);
        TypeCatalog.Current.ServicePointTypes.Select(t => t.Key).ShouldContain("flerbostadshus");

        // A free spot next to an access site in the network.
        var site = await Row(db, "SELECT id, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site WHERE lifecycle = 'in_service' ORDER BY id LIMIT 1");
        var (siteId, sx, sy) = ((long)site[0]!, (double)site[1]!, (double)site[2]!);
        var plan = await PlanAsync(client, "Anslutningar i test");

        var created = await AddAsync(client, plan, new
        {
            kind = "create_service_point",
            code = "AP-T-0001",
            typeKey = "flerbostadshus",
            units = 24,
            address = "Testgatan 1",
            x = sx + 120,
            y = sy + 40
        });
        created.Summary.ShouldBe("Ny anslutningspunkt AP-T-0001 (Flerbostadshus, 24 enheter), Testgatan 1");
        var planned = created.Target!.Id;
        planned.ShouldBeLessThan(0);
        var connected = await AddAsync(client, plan, new { kind = "update_service_point", objectId = planned, status = "connected", siteId });
        connected.Summary.ShouldContain("status ansluten");
        connected.Summary.ShouldContain("anslut till");

        // Checked like other operations: a known type, a free code, and a site for a connected point.
        (await Add(client, plan, new { kind = "create_service_point", code = "AP-T-0002", typeKey = "slott", x = sx, y = sy })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Add(client, plan, new { kind = "create_service_point", code = "AP-T-0001", typeKey = "villa", x = sx, y = sy })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Add(client, plan, new { kind = "create_service_point", code = "AP-T-0003", typeKey = "villa", status = "connected", x = sx, y = sy }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // An import: comma or semicolon, SWEREF 99 TM or WGS 84; checked first, all or none.
        var (lon, lat) = await LonLatAsync(db, sx + 300, sy - 200);
        var csv = new StringBuilder("code;type;address;x;y;lat;lon\n")
            .AppendLine(CultureInfo.InvariantCulture, $"AP-T-0010;villa;Testvägen 10;{sx + 200};{sy + 100};;")
            .AppendLine(CultureInfo.InvariantCulture, $"AP-T-0011;radhus;Testvägen 12;{sx + 220};{sy + 100};;")
            .ToString();
        var dry = await ImportAsync(client, plan, "csv", csv, dryRun: true);
        (dry.ServicePoints, dry.Units, dry.Errors.Count).ShouldBe((2, 2, 0));
        var wgs = $"code,type,units,lat,lon\nAP-T-0012,flerbostadshus,8,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}\n";
        var bad = await ImportAsync(client, plan, "csv", wgs + "AP-T-0013,slott,1,59,18\n", dryRun: false);
        bad.Errors.ShouldHaveSingleItem().Row.ShouldBe(3);
        (await ImportAsync(client, plan, "csv", csv, dryRun: false)).ServicePoints.ShouldBe(2);
        (await ImportAsync(client, plan, "csv", wgs, dryRun: false)).Units.ShouldBe(8);
        var again = await ImportAsync(client, plan, "csv", csv, dryRun: false);
        (again.ServicePoints, again.Skipped).ShouldBe((0, 2));
        var geojson = JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = new[] { new { type = "Feature", geometry = new { type = "Point", coordinates = new[] { sx + 260, sy + 90 } }, properties = new { code = "AP-T-0014", type = "foretag" } } },
        });
        (await ImportAsync(client, plan, "geojson", geojson, dryRun: false)).ServicePoints.ShouldBe(1);

        // The plan's map draws them; applying writes them, connected to the site.
        var view = await client.GetFromJsonAsync<JsonElement>($"/api/plans/{plan}/view", Ct);
        view.GetProperty("planned").GetProperty("servicePoints").GetArrayLength().ShouldBe(5);
        (await client.PostAsync($"/api/plans/{plan}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = await Scalar<long>(db, "SELECT id FROM service_point WHERE code = 'AP-T-0001'");
        var point = (await client.GetFromJsonAsync<ServicePointDetail>($"/api/service-points/{id}", Ct))!;
        (point.Status, point.Units, point.TypeName, point.Site!.Id).ShouldBe(("connected", 24, "Flerbostadshus", siteId));
        var near = await Scalar<string>(db, "SELECT code FROM service_point WHERE code = 'AP-T-0012'");
        near.ShouldBe("AP-T-0012");
        (await Scalar<long>(db, "SELECT count(*) FROM service_point")).ShouldBe(5);
        (await Scalar<long>(db, $"SELECT count(*) FROM operation WHERE command = 'import_service_points'")).ShouldBe(3);

        // Read: the list with counts, search by address, the map from street level, and a move and disconnection in a new plan.
        var list = (await client.GetFromJsonAsync<ServicePointList>(
            $"/api/service-points?near={(sx + 200).ToString(CultureInfo.InvariantCulture)},{sy.ToString(CultureInfo.InvariantCulture)}&radius=2000", Ct))!;
        list.Total.ShouldBe(5);
        list.Units.ShouldBe(24 + 1 + 1 + 8 + 1);
        list.ByStatus["connected"].ShouldBe(1);
        var hits = (await client.GetFromJsonAsync<List<SearchHit>>("/api/search?q=Testvägen", Ct))!;
        hits.ShouldContain(h => h.Type == "service-point" && h.Code == "AP-T-0010");
        await TileAsync(client, sx + 120, sy + 40, 9, "AP-T-0001", true);
        await TileAsync(client, sx + 120, sy + 40, 8, "AP-T-0001", false);

        var second = await PlanAsync(client, "Flytt i test");
        await AddAsync(client, second, new { kind = "update_service_point", objectId = id, x = sx + 150, y = sy + 50, disconnect = true, status = "ordered" });
        (await client.PostAsync($"/api/plans/{second}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var moved = (await client.GetFromJsonAsync<ServicePointDetail>($"/api/service-points/{id}", Ct))!;
        (moved.Status, moved.Site, moved.X).ShouldBe(("ordered", null, Math.Round(sx + 150, 1)));
    }

    [Fact]
    public async Task Service_points_follow_the_callers_scopes_and_reach_the_agents()
    {
        var (db, api) = await ApiAsync(63);
        await using var _ = api;
        // One point inside Region Nord and one outside, written directly.
        var inside = await Row(db, """
            SELECT ST_X(c), ST_Y(c) FROM access_scope a, LATERAL ST_PointOnSurface(a.area) c WHERE a.key = 'region-nord'
            """);
        var outside = await Row(db, """
            SELECT ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)) FROM site s
            WHERE NOT EXISTS (SELECT 1 FROM scope_site z WHERE z.scope_key = 'region-nord' AND z.site_id = s.id) ORDER BY s.id LIMIT 1
            """);
        await Exec(db, $"""
            INSERT INTO service_point (code, type_key, address, geom) VALUES
              ('AP-NORD', 'villa', 'Nordvägen 1', ST_SetSRID(ST_MakePoint({Num(inside[0])}, {Num(inside[1])}), 3006)),
              ('AP-SYD', 'villa', 'Sydvägen 1', ST_SetSRID(ST_MakePoint({Num(outside[0])}, {Num(outside[1])}), 3006))
            """);
        var (north, south) = (await Scalar<long>(db, "SELECT id FROM service_point WHERE code = 'AP-NORD'"), await Scalar<long>(db, "SELECT id FROM service_point WHERE code = 'AP-SYD'"));

        using var regional = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await regional.GetAsync($"/api/service-points/{north}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await regional.GetAsync($"/api/service-points/{south}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var seen = (await regional.GetFromJsonAsync<ServicePointList>("/api/service-points", Ct))!;
        seen.Items.Select(p => p.Code).ShouldBe(["AP-NORD"]);
        (await regional.GetFromJsonAsync<List<SearchHit>>("/api/search?q=AP-SYD", Ct))!.ShouldNotContain(h => h.Type == "service-point");

        var http = NetworkFixture.Client(api);
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: true),
            cancellationToken: Ct);
        var found = await mcp.CallToolAsync("find_service_points", new Dictionary<string, object?> { ["status"] = "planned" }, cancellationToken: Ct);
        Text(found).ShouldContain("\"total\":2");
        var one = await mcp.CallToolAsync("get_object", new Dictionary<string, object?> { ["reference"] = $"service-point:{north}" }, cancellationToken: Ct);
        Text(one).ShouldContain("AP-NORD");
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));

    private static string Num(object? value) => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static async Task TileAsync(HttpClient client, double x, double y, int z, string code, bool shown)
    {
        // The tile over the point, in the grid the API and the web share.
        var size = (Cmdb.Api.Features.Map.TileGrid.MaxX - Cmdb.Api.Features.Map.TileGrid.MinX) / (double)(1 << z);
        var tx = (int)((x - Cmdb.Api.Features.Map.TileGrid.MinX) / size);
        var ty = (int)((Cmdb.Api.Features.Map.TileGrid.MaxY - y) / size);
        var tile = await client.GetByteArrayAsync($"/api/tiles/{z}/{tx}/{ty}", Ct);
        (tile.AsSpan().IndexOf(Encoding.UTF8.GetBytes(code)) >= 0).ShouldBe(shown);
    }

    private static async Task<long> PlanAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/plans", new { name }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> Add(HttpClient client, long plan, object op) => client.PostAsJsonAsync($"/api/plans/{plan}/operations", op, Ct);

    private static async Task<PlanOperationView> AddAsync(HttpClient client, long plan, object op)
    {
        var response = await Add(client, plan, op);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanOperationView>(Ct))!;
    }

    private static async Task<ServicePointImportResult> ImportAsync(HttpClient client, long plan, string format, string content, bool dryRun)
    {
        var response = await client.PostAsJsonAsync($"/api/plans/{plan}/service-points/import", new { format, content, dryRun }, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ServicePointImportResult>(Ct))!;
    }

    private static async Task<(double Lon, double Lat)> LonLatAsync(NpgsqlDataSource db, double x, double y)
    {
        var row = await Row(db, $"SELECT ST_X(p), ST_Y(p) FROM ST_Transform(ST_SetSRID(ST_MakePoint({x.ToString(CultureInfo.InvariantCulture)}, {y.ToString(CultureInfo.InvariantCulture)}), 3006), 4326) p");
        return ((double)row[0]!, (double)row[1]!);
    }

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> ApiAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (db, api);
    }

    private static async Task<object?[]> Row(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        var row = new object?[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return row;
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}

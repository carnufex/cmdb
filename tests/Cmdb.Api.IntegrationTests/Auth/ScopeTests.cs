using System.Net;
using System.Net.Http.Json;
using System.Text;
using Cmdb.Api.Features.CurrentUser;
using Cmdb.Api.Features.Equipment;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Query;
using Cmdb.Api.Features.Search;
using Cmdb.Api.Features.Sites;
using Cmdb.Api.Features.Trace;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Auth;

/// <summary>
/// Access scopes (#22) on every read surface, with the demo users: what each may see, and above all what not.
/// The small generated network (seed 1) spans the country, so all three scopes have something in and out.
/// </summary>
public sealed class ScopeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string North = "ST_Y(ST_PointOnSurface(s.geom)) > 6950000";
    private const string ProjectBox = "ST_Intersects(s.geom, ST_MakeEnvelope(250000, 6450000, 380000, 6580000, 3006))";

    [Fact]
    public async Task Without_a_granted_scope_nothing_is_visible()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api, "cmdb-nobody", groups: []);
        var site = await Scalar(db, "SELECT id FROM site s ORDER BY id LIMIT 1");
        var code = await Text(db, $"SELECT code FROM site WHERE id = {site}");

        (await client.GetFromJsonAsync<MeResponse>("/api/me", Ct))!.Scopes.ShouldBeEmpty();
        (await client.GetAsync($"/api/sites/{site}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<List<SearchHit>>($"/api/search?q={code}", Ct))!.ShouldBeEmpty();
        (await Query(client)).Total.ShouldBe(0);
        (await client.GetAsync($"/api/summary/site/{site}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<Impact>($"/api/sites/{site}/impact", Ct))!.Services.ShouldBeEmpty();
        Encoding.UTF8.GetString(await client.GetByteArrayAsync("/api/tiles/0/0/0", Ct)).ShouldNotContain("sites");
    }

    [Fact]
    public async Task Region_nord_sees_the_north_and_nothing_south_of_it()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var inside = await Scalar(db, $"SELECT s.id FROM site s WHERE {North} AND s.site_type = 'aggregation' ORDER BY s.id LIMIT 1");
        var outside = await Scalar(db, $"SELECT s.id FROM site s WHERE NOT {North} AND s.site_type = 'hub' ORDER BY s.id LIMIT 1");
        var outsideCode = await Text(db, $"SELECT code FROM site WHERE id = {outside}");
        var outsideEquipment = await Scalar(db, $"SELECT id FROM equipment WHERE site_id = {outside} LIMIT 1");

        (await client.GetFromJsonAsync<MeResponse>("/api/me", Ct))!.Scopes.ShouldBe(["Region Nord"]);
        (await client.GetAsync($"/api/sites/{inside}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"/api/sites/{outside}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/equipment/{outsideEquipment}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/summary/site/{outside}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/sites/{outside}/graph", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<List<SearchHit>>($"/api/search?q={outsideCode}", Ct))!.ShouldNotContain(h => h.Code == outsideCode);

        var found = await Query(client);
        found.Total.ShouldBeGreaterThan(0);
        var allowed = (await Ids(db, $"SELECT s.id FROM site s WHERE {North}")).ToHashSet();
        found.Points.ShouldAllBe(p => allowed.Contains((long)p[0]));

        var neighbours = (await client.GetFromJsonAsync<List<Neighbour>>($"/api/sites/{inside}/neighbourhood?hops=3", Ct))!;
        neighbours.ShouldAllBe(n => allowed.Contains(n.Id));
        var graph = (await client.GetFromJsonAsync<SiteGraph>($"/api/sites/{inside}/graph", Ct))!;
        graph.Nodes.ShouldAllBe(n => allowed.Contains(n.Id));
    }

    [Fact]
    public async Task A_cable_across_the_region_edge_shows_with_its_far_end_as_a_placeholder()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var cable = await Scalar(db, $"""
            SELECT c.id FROM cable c JOIN site a ON a.id = c.a_site_id JOIN site b ON b.id = c.b_site_id
            WHERE (ST_Y(ST_PointOnSurface(a.geom)) > 6950000) <> (ST_Y(ST_PointOnSurface(b.geom)) > 6950000)
            ORDER BY c.id LIMIT 1
            """);

        var detail = (await client.GetFromJsonAsync<Cmdb.Api.Features.Cables.CableDetail>($"/api/cables/{cable}", Ct))!;

        new[] { detail.A, detail.B }.Count(s => s.Id == 0 && s.Name == "Utanför ditt omfång").ShouldBe(1);
    }

    [Fact]
    public async Task A_trace_stops_at_the_region_edge_with_a_placeholder()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        // A physical circuit from a port in the north that ends in the south.
        var terminal = await Scalar(db, $"""
            SELECT r.a_terminal_id FROM circuit r
            JOIN port pa ON pa.terminal_id = r.a_terminal_id JOIN equipment ea ON ea.id = pa.equipment_id JOIN site s ON s.id = ea.site_id
            JOIN port pb ON pb.terminal_id = r.b_terminal_id JOIN equipment eb ON eb.id = pb.equipment_id JOIN site sb ON sb.id = eb.site_id
            WHERE r.layer = 'physical' AND {North} AND ST_Y(ST_PointOnSurface(sb.geom)) <= 6950000
            ORDER BY r.id LIMIT 1
            """);

        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={terminal}", Ct))!;

        trace.Physical!.Complete.ShouldBeFalse();
        trace.Physical.Ends.ShouldContain("boundary");
        var placeholder = trace.Physical.Hops.Single(h => h.Kind == "hidden");
        placeholder.TerminalId.ShouldBe(0);
        placeholder.Site.ShouldBeNull();
        trace.Physical.Hops.Where(h => h.Kind != "hidden").ShouldAllBe(h => h.Site != null);

        using var full = NetworkFixture.Client(api);
        (await full.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={terminal}", Ct))!.Physical!.Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task Projekt_a_sees_only_radio_sites_and_cabinets_in_its_area_and_no_serial_numbers()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api, "cmdb-demo-projekt", ["cmdb-projekt-a"]);
        var radio = await Scalar(db, $"SELECT s.id FROM site s WHERE {ProjectBox} AND s.site_type = 'radio' ORDER BY s.id LIMIT 1");
        var aggregation = await Scalar(db, $"SELECT s.id FROM site s WHERE {ProjectBox} AND s.site_type <> 'radio' AND s.site_type <> 'cabinet' ORDER BY s.id LIMIT 1");
        var radioEquipment = await Scalar(db, $"SELECT e.id FROM equipment e WHERE e.site_id = {radio} AND e.attributes ? 'serialNumber' LIMIT 1");

        (await client.GetAsync($"/api/sites/{radio}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"/api/sites/{aggregation}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var equipment = (await client.GetFromJsonAsync<EquipmentDetail>($"/api/equipment/{radioEquipment}", Ct))!;
        equipment.Attributes.TryGetProperty("serialNumber", out _).ShouldBeFalse();

        using var full = NetworkFixture.Client(api);
        (await full.GetFromJsonAsync<EquipmentDetail>($"/api/equipment/{radioEquipment}", Ct))!.Attributes.TryGetProperty("serialNumber", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Impact_counts_services_outside_the_scope_without_naming_them()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var region = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        using var full = NetworkFixture.Client(api);
        // A northern aggregation site carries services whose far ends are in the south.
        var site = await Scalar(db, $"SELECT s.id FROM site s WHERE {North} AND s.site_type = 'aggregation' ORDER BY s.id LIMIT 1");

        var all = (await full.GetFromJsonAsync<Impact>($"/api/sites/{site}/impact", Ct))!;
        var scoped = (await region.GetFromJsonAsync<Impact>($"/api/sites/{site}/impact", Ct))!;

        (scoped.Services.Count + scoped.HiddenServices).ShouldBe(all.Services.Count);
        scoped.Services.Count.ShouldBeLessThanOrEqualTo(all.Services.Count);
        scoped.Circuits.ShouldBeLessThanOrEqualTo(all.Circuits);
    }

    private static async Task<SiteQueryResult> Query(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/query/sites", new { limit = 50 }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SiteQueryResult>(Ct))!;
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (long)((await cmd.ExecuteScalarAsync(Ct)) ?? throw new InvalidOperationException($"No row for: {sql}"));
    }

    private static async Task<string> Text(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<long>> Ids(NpgsqlDataSource db, string sql)
    {
        var ids = new List<long>();
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }
}

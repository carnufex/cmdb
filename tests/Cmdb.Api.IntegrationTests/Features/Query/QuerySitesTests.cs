using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Query;

namespace Cmdb.Api.IntegrationTests.Features.Query;

public sealed class QuerySitesTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Finds_sites_by_equipment_attribute_with_every_operator()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var high = await SiteWithAsync("radio", ("acme-rr-4", $$"""{"bandMHz":3500,"transmitPowerW":80,"serialNumber":"SN{{tag}}-A1"}"""));
        var low = await SiteWithAsync("radio", ("acme-rr-2", $$"""{"bandMHz":700,"transmitPowerW":20,"serialNumber":"XX{{tag}}-B2"}"""));

        async Task<IReadOnlyList<long>> Ids(string key, string op, object? value) =>
            [.. (await QueryAsync(new { siteTypes = new[] { "radio" }, equipment = new[] { new { category = "radio", attribute = new { key, op, value } } } })).Ids];

        (await Ids("bandMHz", "eq", 3500)).ShouldContain(high);
        (await Ids("bandMHz", "eq", 3500)).ShouldNotContain(low);
        (await Ids("bandMHz", "neq", 3500)).ShouldContain(low);
        (await Ids("bandMHz", "neq", 3500)).ShouldNotContain(high);
        (await Ids("transmitPowerW", "gt", 50)).ShouldContain(high);
        (await Ids("transmitPowerW", "gt", 50)).ShouldNotContain(low);
        (await Ids("transmitPowerW", "lte", 20)).ShouldContain(low);
        (await Ids("transmitPowerW", "gte", 80)).ShouldContain(high);
        (await Ids("transmitPowerW", "lt", 80)).ShouldNotContain(high);
        (await Ids("serialNumber", "prefix", $"SN{tag}")).ShouldBe([high]);
        (await Ids("serialNumber", "contains", $"{tag}-b")).ShouldBe([low]);
        (await Ids("bandMHz", "exists", null)).ShouldContain(high);
    }

    [Theory]
    [InlineData("Gävle")]
    [InlineData("gavle")]
    [InlineData("Gävle kommun")]
    public async Task Finds_the_sites_in_a_place_and_nowhere_else(string place)
    {
        var inside = await SiteAtAsync(617000, 6727000);
        var outside = await SiteAtAsync(600000, 6600000);

        var result = await QueryAsync(new { place, limit = 1000 });

        result.Ids.ShouldContain(inside);
        result.Ids.ShouldNotContain(outside);
    }

    [Fact]
    public async Task Around_a_place_reaches_outside_it_and_no_further()
    {
        // In Gävle, about 15 km outside it, and far away.
        var inside = await SiteAtAsync(617000, 6727000);
        var beside = await SiteAtAsync(650000, 6700000);
        var far = await SiteAtAsync(600000, 6300000);

        var within = (await QueryAsync(new { place = "Gävle", withinKm = 60, limit = 1000 })).Ids.ToList();
        var only = (await QueryAsync(new { place = "Gävle", limit = 1000 })).Ids.ToList();

        within.ShouldContain(inside);
        within.ShouldContain(beside);
        within.ShouldNotContain(far);
        only.ShouldNotContain(beside);
    }

    [Theory]
    [InlineData("""{ "withinKm": 5 }""")]
    [InlineData("""{ "place": "Gävle", "withinKm": 500 }""")]
    [InlineData("""{ "extent": [1, 2, 3] }""")]
    [InlineData("""{ "extent": [5, 5, 1, 1] }""")]
    public async Task A_distance_without_a_place_or_a_bad_area_is_an_error(string body)
    {
        await factory.RefreshScopesAsync();
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsync("/api/query/sites", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_code_prefix_and_a_region_narrow_the_answer()
    {
        var south = await SiteAtAsync(450000, 6300000);
        var north = await SiteAtAsync(700000, 7300000);
        var code = await Scalar<string>($"SELECT code FROM site WHERE id = {south}");

        (await QueryAsync(new { codePrefix = code.ToLowerInvariant(), limit = 1000 })).Ids.ShouldBe([south]);
        (await QueryAsync(new { codePrefix = "INGET-SADANT", limit = 1000 })).Total.ShouldBe(0);
        var northern = (await QueryAsync(new { region = "norr", limit = 1000 })).Ids.ToList();
        northern.ShouldContain(north);
        northern.ShouldNotContain(south);
        (await QueryAsync(new { region = "söder", limit = 1000 })).Ids.ShouldContain(south);

        await factory.RefreshScopesAsync();
        using var client = factory.CreateAuthenticatedClient();
        (await client.PostAsJsonAsync("/api/query/sites", new { region = "ovanfor" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/query/sites", new { codePrefix = "%" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_map_gets_as_many_positions_as_it_asks_for_and_the_list_stays_short()
    {
        await SiteAtAsync(450000, 6500000);
        await SiteAtAsync(460000, 6500000);

        var one = await QueryAsync(new { limit = 1, pointLimit = 1 });
        one.Points.Count.ShouldBe(1);
        one.Total.ShouldBeGreaterThan(1);
        var all = await QueryAsync(new { limit = 1, pointLimit = 50000 });
        all.Sites.Count.ShouldBe(1);
        all.Points.Count.ShouldBe((int)all.Total);

        await factory.RefreshScopesAsync();
        using var client = factory.CreateAuthenticatedClient();
        (await client.PostAsJsonAsync("/api/query/sites", new { pointLimit = 50001 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_area_finds_the_sites_in_it()
    {
        var inside = await SiteAtAsync(450000, 6500000);
        var outside = await SiteAtAsync(470000, 6500000);

        var ids = (await QueryAsync(new { extent = new[] { 449000.0, 6499000, 451000, 6501000 }, limit = 1000 })).Ids.ToList();

        ids.ShouldContain(inside);
        ids.ShouldNotContain(outside);
    }

    [Fact]
    public async Task Finds_sites_by_what_their_equipment_draws_and_adds_it_up()
    {
        var hungry = await SiteWithAsync("aggregation", ("acme-sdh-63", """{"powerDrawW":120}"""), ("acme-sdh-63", """{"powerDrawW":30}"""));
        var modest = await SiteWithAsync("aggregation", ("acme-sdh-63", """{"powerDrawW":30}"""));

        var equipment = new[] { new { attribute = new { key = "powerDrawW", op = "gt", value = 35 } } };
        var ids = (await QueryAsync(new { equipment, limit = 1000 })).Ids.ToList();

        ids.ShouldContain(hungry);
        ids.ShouldNotContain(modest);

        await factory.RefreshScopesAsync();
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/query/sites/summary", new { query = new { equipment, limit = 1 }, sumAttribute = "powerDrawW" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var summary = (await response.Content.ReadFromJsonAsync<Cmdb.Api.Features.Query.SiteSummary>(Ct))!;
        summary.Total.ShouldBeGreaterThanOrEqualTo(1);
        summary.Sum.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(150);
    }

    [Fact]
    public async Task An_unknown_place_is_an_error_and_not_an_empty_answer()
    {
        await factory.RefreshScopesAsync();
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/query/sites", new { place = "Ankeborg" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Ankeborg");
    }

    [Fact]
    public async Task Combines_conditions_with_and_and_counts_equipment()
    {
        var both = await SiteWithAsync("aggregation", ("acme-sdh-63", "{}"), ("acme-ant-4p", """{"azimuthDeg":0}"""), ("acme-ant-4p", """{"azimuthDeg":120}"""), ("acme-ant-4p", """{"azimuthDeg":240}"""));
        var oneAntenna = await SiteWithAsync("aggregation", ("acme-sdh-63", "{}"), ("acme-ant-4p", """{"azimuthDeg":0}"""));

        var result = await QueryAsync(new
        {
            siteTypes = new[] { "aggregation" },
            equipment = new object[] { new { typeKey = "acme-sdh-63" }, new { category = "antenna", minCount = 3 } },
        });

        result.Ids.ShouldContain(both);
        result.Ids.ShouldNotContain(oneAntenna);
        result.Total.ShouldBe(result.Points.Count);
        result.Sites.Single(s => s.Id == both).Matching.ShouldBe(1);
    }

    [Fact]
    public async Task Filters_on_site_fields_and_on_services_passing_through()
    {
        var planned = await SiteWithAsync("cabinet", ("acme-ax-24", "{}"));
        await Exec($"UPDATE site SET lifecycle = 'planned' WHERE id = {planned}");
        var withService = await SiteWithAsync("cabinet", ("acme-ax-24", "{}"));
        await ServiceThroughAsync(withService, "ethernet");

        (await QueryAsync(new { lifecycles = new[] { "planned" } })).Ids.ShouldContain(planned);
        (await QueryAsync(new { lifecycles = new[] { "planned" } })).Ids.ShouldNotContain(withService);
        (await QueryAsync(new { serviceTypes = new[] { "ethernet" } })).Ids.ShouldContain(withService);
        (await QueryAsync(new { serviceTypes = new[] { "ethernet" } })).Ids.ShouldNotContain(planned);
    }

    [Fact]
    public async Task Finds_sites_by_their_own_attributes()
    {
        var weak = await SiteWithAsync("cabinet", ("acme-ax-24", "{}"));
        var strong = await SiteWithAsync("cabinet", ("acme-ax-24", "{}"));
        var tag = Guid.NewGuid().ToString("N")[..8];
        await Exec($$"""UPDATE site SET attributes = '{"backupHours": 2, "aliases": ["Svag {{tag}}"]}' WHERE id = {{weak}}""");
        await Exec($$"""UPDATE site SET attributes = '{"backupHours": 48}' WHERE id = {{strong}}""");

        async Task<IReadOnlyList<long>> Ids(params object[] conditions) =>
            [.. (await QueryAsync(new { siteTypes = new[] { "cabinet" }, siteAttributes = conditions })).Ids];

        (await Ids(new { key = "backupHours", op = "lt", value = 4 })).ShouldContain(weak);
        (await Ids(new { key = "backupHours", op = "lt", value = 4 })).ShouldNotContain(strong);
        (await Ids(new { key = "backupHours", op = "gte", value = 48 })).ShouldContain(strong);
        (await Ids(new { key = "aliases", op = "contains", value = tag })).ShouldBe([weak]);
        (await Ids(new { key = "backupHours", op = "exists" }, new { key = "backupHours", op = "gt", value = 10 })).ShouldNotContain(weak);
    }

    [Theory]
    [InlineData("""{ "siteAttributes": [ { "key": "noSuchKey", "op": "eq", "value": 1 } ] }""")]
    [InlineData("""{ "siteAttributes": [ { "key": "backupHours", "op": "gt", "value": "many" } ] }""")]
    [InlineData("""{ "siteAttributes": [ { "key": "backupHours", "op": "like", "value": 1 } ] }""")]
    public async Task Rejects_unknown_site_attributes_and_operators(string body)
    {
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/query/sites", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Attributes_hidden_by_the_scope_cannot_be_searched()
    {
        await Exec("""
            INSERT INTO access_scope (key, name, area, site_types, hidden_attributes, plans, crossing_mode, groups, db_roles, reason, granted_by, approved_by)
            VALUES ('test-dolda-attribut', 'Test dolda attribut', ST_MakeEnvelope(0, 6000000, 1000000, 8000000, 3006), '{}',
                    '{backupHours,bandMHz}', '{}', 'whole', '{cmdb-test-dolda-attribut}', '{}', 'test', 'a', 'b')
            ON CONFLICT (key) DO NOTHING
            """);
        await factory.RefreshScopesAsync();
        using var client = factory.CreateAuthenticatedClient("dold", ["cmdb-test-dolda-attribut"]);

        foreach (var query in new object[]
        {
            new { siteAttributes = new[] { new { key = "backupHours", op = "lt", value = 4 } } },
            new { equipment = new[] { new { category = "radio", attribute = new { key = "bandMHz", op = "eq", value = 3500 } } } },
        })
        {
            var response = await client.PostAsJsonAsync("/api/query/sites", query, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("has the attribute");
        }
        (await client.PostAsJsonAsync("/api/query/sites", new { siteAttributes = new[] { new { key = "aliases", op = "exists" } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("""{ "equipment": [ { "attribute": { "key": "noSuchKey", "op": "eq", "value": 1 } } ] }""")]
    [InlineData("""{ "equipment": [ { "attribute": { "key": "bandMHz", "op": "like", "value": 1 } } ] }""")]
    [InlineData("""{ "equipment": [ { "attribute": { "key": "bandMHz", "op": "gt", "value": "high" } } ] }""")]
    [InlineData("""{ "equipment": [ { "attribute": { "key": "band'; DROP TABLE site; --", "op": "eq", "value": 1 } } ] }""")]
    [InlineData("""{ "equipment": [ { "typeKey": "acme-nope" } ] }""")]
    [InlineData("""{ "equipment": [ { } ] }""")]
    [InlineData("""{ "lifecycles": [ "gone" ] }""")]
    [InlineData("""{ "limit": 5000 }""")]
    public async Task Rejects_unknown_fields_operators_and_values(string body)
    {
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/query/sites", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Describes_the_fields_from_the_catalog()
    {
        using var client = factory.CreateAuthenticatedClient();

        var fields = (await client.GetFromJsonAsync<QueryFields>("/api/query/fields", Ct))!;

        var radio = fields.Categories.Single(c => c.Key == "radio");
        radio.Attributes.Single(a => a.Key == "bandMHz").Values!.Select(v => v.GetInt32()).ShouldContain(3500);
        radio.Attributes.Single(a => a.Key == "transmitPowerW").Type.ShouldBe("number");
        fields.Types.ShouldContain(t => t.Key == "acme-ax-48p" && t.Category == "switch");
        fields.Lifecycles.ShouldContain("in_service");
        var hub = fields.SiteTypeDetails!.Single(t => t.Key == "hub");
        hub.Attributes!.Single(a => a.Key == "backupHours").Title.ShouldBe("Reservkraft (timmar)");
        fields.ServiceTypeDetails!.Single(t => t.Key == "ethernet").Attributes!.Single().Key.ShouldBe("bandwidthMbps");
        fields.CableTypes!.Single(t => t.Key == "fiber-12").Attributes!.Select(a => a.Key).ShouldBe(["installationYear", "owner"], ignoreOrder: true);
    }

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/query/sites", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<SiteQueryResult> QueryAsync(object query)
    {
        await factory.RefreshScopesAsync();
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/query/sites", query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SiteQueryResult>(Ct))!;
    }

    private Task<long> SiteAtAsync(int x, int y) => Scalar<long>($"""
        INSERT INTO site (code, name, site_type, geom, lifecycle)
        VALUES ('Q-{Guid.NewGuid():N}', 'Query test', 'cabinet', 'SRID=3006;POINT({x} {y})', 'in_service') RETURNING id
        """);

    private async Task<long> SiteWithAsync(string siteType, params (string Type, string Attributes)[] equipment)
    {
        var site = await Scalar<long>($"""
            INSERT INTO site (code, name, site_type, geom, lifecycle)
            VALUES ('Q-{Guid.NewGuid():N}', 'Query test', '{siteType}', 'SRID=3006;POINT(600000 6600000)', 'in_service') RETURNING id
            """);
        var rack = await Scalar<long>($"INSERT INTO location (site_id, kind, name) VALUES ({site}, 'rack', 'R1') RETURNING id");
        foreach (var (type, attributes) in equipment)
        {
            await Exec($"""
                INSERT INTO equipment (equipment_type_id, site_id, location_id, name, attributes, lifecycle)
                SELECT id, {site}, {rack}, 'Q {type}', '{attributes}', 'in_service' FROM equipment_type WHERE key = '{type}'
                """);
        }
        return site;
    }

    private async Task ServiceThroughAsync(long site, string serviceType)
    {
        var equipment = await Scalar<long>($"SELECT id FROM equipment WHERE site_id = {site} LIMIT 1");
        var terminal = await Scalar<long>("INSERT INTO terminal (kind) VALUES ('port') RETURNING id");
        await Exec($"INSERT INTO port (terminal_id, equipment_id, name, port_type, position) VALUES ({terminal}, {equipment}, 'q1', 'RJ45', 1)");
        var circuit = await Scalar<long>($"INSERT INTO circuit (code, layer, a_terminal_id, b_terminal_id) VALUES ('QC-{Guid.NewGuid():N}', 'logical', {terminal}, {terminal}) RETURNING id");
        await Exec($"INSERT INTO circuit_hop (circuit_id, seq, terminal_id) VALUES ({circuit}, 0, {terminal})");
        var service = await Scalar<long>($"INSERT INTO service (code, name, service_type) VALUES ('QS-{Guid.NewGuid():N}', 'Query test', '{serviceType}') RETURNING id");
        await Exec($"INSERT INTO service_circuit (service_id, circuit_id) VALUES ({service}, {circuit})");
    }

    private async Task Exec(string sql)
    {
        await using var cmd = factory.Db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var cmd = factory.Db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

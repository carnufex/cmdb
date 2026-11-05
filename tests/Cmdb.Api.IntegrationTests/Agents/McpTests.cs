using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cmdb.Api.IntegrationTests.Agents;

/// <summary>The MCP server as an agent sees it, through the SDK's own client (ADR-0011, #61).</summary>
public sealed class McpTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_the_read_only_tools_resources_and_prompts()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var resources = await client.ListResourcesAsync(cancellationToken: Ct);
        var prompts = await client.ListPromptsAsync(cancellationToken: Ct);

        tools.Select(t => t.Name).Order().ShouldBe([
            "add_route_to_plan", "add_to_plan", "check_plan_classification", "connect_ports", "create_plan",
            "create_site_from_template", "describe_catalog", "describe_classifications", "fault_impact", "find_capacity",
            "find_service_points", "find_sites", "find_station", "get_classification", "get_history", "get_object",
            "get_project", "get_reconciliation", "impact", "import_to_plan", "list_incidents", "list_plans", "list_projects",
            "list_reconciliations", "list_reservations", "list_risks", "list_site_templates", "my_access", "neighbourhood",
            "open_object", "plan_materials", "preview_plan", "request_change", "risk_details", "search", "show_on_map",
            "splice_ports_to_cable", "station_overview", "suggest_route", "summarize_sites", "terminate_cable", "trace"]);
        // Writing tools only write plans (#64): none is destructive, and none applies a plan.
        tools.Where(t => t.ProtocolTool.Annotations!.ReadOnlyHint != true).Select(t => t.Name).Order()
            .ShouldBe(["add_route_to_plan", "add_to_plan", "connect_ports", "create_plan", "create_site_from_template", "import_to_plan", "request_change", "splice_ports_to_cable", "terminate_cable"]);
        tools.ShouldAllBe(t => t.ProtocolTool.Annotations!.DestructiveHint != true);
        resources.Select(r => r.Uri).ShouldBe(["cmdb://docs/domanmodell", "cmdb://docs/plan", "cmdb://docs/arkitektur", "cmdb://changelog"], ignoreOrder: true);
        var changelog = await client.ReadResourceAsync("cmdb://changelog", cancellationToken: Ct);
        ((ModelContextProtocol.Protocol.TextResourceContents)changelog.Contents[0]).Text.ShouldContain("# Händelselogg");
        prompts.Select(p => p.Name).ShouldBe(["cable_cut_impact", "find_sites_by_equipment"], ignoreOrder: true);
        client.ServerInstructions.ShouldNotBeNull().ShouldContain("SYNTHETIC");
    }

    [Fact]
    public async Task Search_get_object_and_neighbourhood_give_references_and_links()
    {
        var net = await NetworkAsync();
        await using var client = await ConnectAsync();

        var search = await CallAsync(client, "search", new() { ["query"] = net.CodeA });
        var hit = search.GetProperty("hits")[0];
        hit.GetProperty("ref").GetString().ShouldBe($"site:{net.SiteA}");
        hit.GetProperty("url").GetString().ShouldEndWith($"/?p=site:{net.SiteA}");

        var byRef = await CallAsync(client, "get_object", new() { ["reference"] = $"site:{net.SiteA}" });
        var byCode = await CallAsync(client, "get_object", new() { ["reference"] = net.CodeA });
        byRef.GetProperty("detail").GetProperty("code").GetString().ShouldBe(net.CodeA);
        byCode.GetProperty("ref").GetString().ShouldBe($"site:{net.SiteA}");
        byRef.GetProperty("detail").GetProperty("cables").GetArrayLength().ShouldBe(1);

        var neighbours = await CallAsync(client, "neighbourhood", new() { ["siteId"] = net.SiteA, ["hops"] = 1 });
        var only = neighbours.GetProperty("sites").EnumerateArray().Single();
        only.GetProperty("ref").GetString().ShouldBe($"site:{net.SiteB}");
        only.GetProperty("viaCable").GetString().ShouldBe(net.CableCode);
    }

    [Fact]
    public async Task Show_on_map_counts_what_the_web_app_will_mark_and_lists_only_the_first()
    {
        await using var client = await ConnectAsync();

        // A condition only this test's sites meet: other tests add sites to the same database meanwhile.
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        for (var i = 0; i < 12; i++)
        {
            await Scalar<long>($"INSERT INTO site (code, name, site_type, geom, lifecycle) VALUES ('SHOW{tag}-{i}', 'Visa', 'cabinet', 'SRID=3006;POINT(500000 6500000)', 'in_service') RETURNING id");
        }
        var condition = new Dictionary<string, object?> { ["codePrefix"] = $"SHOW{tag}" };
        var all = await CallAsync(client, "find_sites", new(condition) { ["limit"] = 1 });
        var shown = await CallAsync(client, "show_on_map", condition);

        shown.GetProperty("total").GetInt64().ShouldBe(all.GetProperty("total").GetInt64());
        shown.GetProperty("sites").GetArrayLength().ShouldBeLessThanOrEqualTo(10);
        shown.GetProperty("sites")[0].GetProperty("ref").GetString().ShouldStartWith("site:");
    }

    [Fact]
    public async Task A_change_request_is_a_draft_until_a_person_says_yes()
    {
        var net = await NetworkAsync();
        var name = $"Nytt namn {Guid.NewGuid():N}"[..24];
        await using var client = await ConnectAsync();

        var asked = await CallAsync(client, "request_change", new()
        {
            ["title"] = "Byt namn",
            ["operations"] = new object[] { new Dictionary<string, object?> { ["kind"] = "rename", ["target"] = $"site:{net.SiteA}", ["name"] = name } },
        });

        var plan = asked.GetProperty("plan");
        plan.GetProperty("status").GetString().ShouldBe("draft");
        asked.GetProperty("changes").GetArrayLength().ShouldBe(1);
        var id = long.Parse(plan.GetProperty("ref").GetString()!["plan:".Length..], System.Globalization.CultureInfo.InvariantCulture);
        (await Scalar<string>($"SELECT name FROM site WHERE id = {net.SiteA}")).ShouldNotBe(name);

        // The person's yes, with their own token, is what writes it.
        using var person = factory.CreateAuthenticatedClient();
        (await person.PostAsync($"/api/plans/{id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Scalar<string>($"SELECT name FROM site WHERE id = {net.SiteA}")).ShouldBe(name);
    }

    [Fact]
    public async Task A_person_who_may_not_write_cannot_request_a_change()
    {
        await factory.RefreshScopesAsync();
        var http = factory.CreateAuthenticatedClient("cmdb-demo-read", ["cmdb-read"]);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true), cancellationToken: Ct);

        var result = await client.CallToolAsync("request_change", new Dictionary<string, object?>
        {
            ["title"] = "Byt namn",
            ["operations"] = new object[] { new Dictionary<string, object?> { ["kind"] = "rename", ["target"] = "site:1", ["name"] = "X" } },
        }, cancellationToken: Ct);

        result.IsError.ShouldBe(true);
        var access = JsonDocument.Parse(Text(await client.CallToolAsync("my_access", new Dictionary<string, object?>(), cancellationToken: Ct))).RootElement;
        access.GetProperty("mayRequestChanges").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_voice_tools_that_read_answer_a_signed_in_person_without_a_phone_verification()
    {
        var net = await NetworkAsync();
        await using var client = await ConnectAsync();

        var found = await CallAsync(client, "find_station", new() { ["query"] = net.CodeA });
        found[0].GetProperty("station").GetString().ShouldBe($"site:{net.SiteA}");
        var overview = await CallAsync(client, "station_overview", new() { ["station"] = $"site:{net.SiteA}" });
        overview.GetProperty("code").GetString().ShouldBe(net.CodeA);
        var fault = await CallAsync(client, "fault_impact", new() { ["reference"] = $"site:{net.SiteA}" });
        fault.GetProperty("priority").GetString().ShouldNotBeNullOrEmpty();
        // Not logged as a phone call.
        (await Scalar<long>("SELECT count(*) FROM voice_tool_call WHERE tool = 'station_overview' AND conversation_id = 'unknown'")).ShouldBe(0);
    }

    [Fact]
    public async Task Summaries_lists_and_history_answer_within_the_tools()
    {
        var net = await NetworkAsync();
        await using var client = await ConnectAsync();

        var perType = await CallAsync(client, "summarize_sites", new() { ["groupBy"] = "siteType" });
        var total = perType.GetProperty("total").GetInt64();
        total.ShouldBeGreaterThan(0);
        perType.GetProperty("groups").EnumerateArray().Sum(g => g.GetProperty("sites").GetInt64()).ShouldBe(total);
        (await CallAsync(client, "summarize_sites", [])).GetProperty("total").GetInt64().ShouldBe(total);
        (await client.CallToolAsync("summarize_sites", new Dictionary<string, object?> { ["groupBy"] = "secret" }, cancellationToken: Ct)).IsError.ShouldBe(true);

        (await CallAsync(client, "my_access", [])).GetProperty("mayRequestChanges").GetBoolean().ShouldBeTrue();
        (await CallAsync(client, "list_incidents", [])).ValueKind.ShouldBe(JsonValueKind.Array);
        (await CallAsync(client, "list_risks", [])).GetProperty("risks").ValueKind.ShouldBe(JsonValueKind.Array);
        var history = await CallAsync(client, "get_history", new() { ["reference"] = $"site:{net.SiteA}" });
        history.GetProperty("ref").GetString().ShouldBe($"site:{net.SiteA}");
        (await CallAsync(client, "open_object", new() { ["reference"] = $"site:{net.SiteA}" })).GetProperty("ref").GetString().ShouldBe($"site:{net.SiteA}");
        (await client.CallToolAsync("open_object", new Dictionary<string, object?> { ["reference"] = "site:999999999" }, cancellationToken: Ct)).IsError.ShouldBe(true);
    }

    [Fact]
    public async Task Find_sites_and_impact_answer_structured_questions()
    {
        var net = await NetworkAsync();
        await using var client = await ConnectAsync();

        var found = await CallAsync(client, "find_sites", new()
        {
            ["siteTypes"] = new[] { "radio" },
            ["equipment"] = new object[] { new { category = "radio", attribute = new { key = "serialNumber", op = "eq", value = net.Serial } } },
        });
        found.GetProperty("total").GetInt64().ShouldBe(1);
        found.GetProperty("sites")[0].GetProperty("ref").GetString().ShouldBe($"site:{net.SiteA}");

        var impact = await CallAsync(client, "impact", new() { ["reference"] = net.CableCode });
        impact.GetProperty("circuits").GetInt32().ShouldBe(1);
        var affected = impact.GetProperty("affectedServices")[0];
        affected.GetProperty("ref").GetString().ShouldBe($"service:{net.Service}");
        affected.GetProperty("via")[0].GetString().ShouldEndWith("(physical)");

        var catalog = await CallAsync(client, "describe_catalog", []);
        catalog.GetProperty("categories").EnumerateArray().ShouldContain(c => c.GetProperty("key").GetString() == "radio");
    }

    [Theory]
    [InlineData("search", """{ "query": "ab" }""", "three characters")]
    [InlineData("get_object", """{ "reference": "toaster:1" }""", "Unknown type")]
    [InlineData("get_object", """{ "reference": "site:999999999" }""", "No site")]
    [InlineData("impact", """{ "reference": "service:1" }""", "cables, equipment, sites and route segments")]
    [InlineData("find_sites", """{ "equipment": [ { "attribute": { "key": "noSuchKey", "op": "eq", "value": 1 } } ] }""", "noSuchKey")]
    [InlineData("show_on_map", """{ "place": "Ankeborg" }""", "Ankeborg")]
    [InlineData("neighbourhood", """{ "siteId": 1, "hops": 9 }""", "hops")]
    public async Task Invalid_arguments_come_back_as_tool_errors_the_agent_can_read(string tool, string arguments, string message)
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)!, cancellationToken: Ct);

        result.IsError.ShouldBe(true);
        Text(result).ShouldContain(message);
    }

    [Fact]
    public async Task Resources_are_the_project_documentation()
    {
        await using var client = await ConnectAsync();

        var model = await client.ReadResourceAsync("cmdb://docs/domanmodell", cancellationToken: Ct);

        ((TextResourceContents)model.Contents[0]).Text.ShouldContain("Domänmodell");
    }

    [Fact]
    public async Task Requires_a_token()
    {
        using var http = factory.CreateClient();

        var response = await http.PostAsync("/mcp", new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<McpClient> ConnectAsync()
    {
        var http = factory.CreateAuthenticatedClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        result.IsError.ShouldNotBe(true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement;
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed record TestNetwork(long SiteA, string CodeA, long SiteB, string CableCode, long Service, string Serial);

    /// <summary>Two sites, a cable between them carrying one circuit and service, and a radio with a unique serial.</summary>
    private async Task<TestNetwork> NetworkAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var codeA = $"MCPA-{tag}";
        var siteA = await Scalar<long>($"INSERT INTO site (code, name, site_type, geom, lifecycle) VALUES ('{codeA}', 'Agent A', 'radio', 'SRID=3006;POINT(610000 6610000)', 'in_service') RETURNING id");
        var siteB = await Scalar<long>($"INSERT INTO site (code, name, site_type, geom, lifecycle) VALUES ('MCPB-{tag}', 'Agent B', 'aggregation', 'SRID=3006;POINT(612000 6612000)', 'in_service') RETURNING id");
        var rack = await Scalar<long>($"INSERT INTO location (site_id, kind, name) VALUES ({siteA}, 'rack', 'R1') RETURNING id");
        var serial = $"SN{tag}";
        await Exec($$"""
            INSERT INTO equipment (equipment_type_id, site_id, location_id, name, attributes, lifecycle)
            SELECT id, {{siteA}}, {{rack}}, 'RR {{tag}}', '{"serialNumber":"{{serial}}","bandMHz":3500}', 'in_service' FROM equipment_type WHERE key = 'acme-rr-4'
            """);
        var cableCode = $"K-{tag}";
        var cable = await Scalar<long>($"""
            INSERT INTO cable (cable_type_id, code, a_site_id, b_site_id, geom, lifecycle)
            SELECT id, '{cableCode}', {siteA}, {siteB}, 'SRID=3006;LINESTRING(610000 6610000, 612000 6612000)', 'in_service' FROM cable_type WHERE key = 'fiber-24'
            RETURNING id
            """);
        var conductor = await Scalar<long>($"INSERT INTO conductor (cable_id, number) VALUES ({cable}, 1) RETURNING id");
        var endA = await Scalar<long>("INSERT INTO terminal (kind) VALUES ('conductor_end') RETURNING id");
        var endB = await Scalar<long>("INSERT INTO terminal (kind) VALUES ('conductor_end') RETURNING id");
        await Exec($"INSERT INTO conductor_end (terminal_id, conductor_id, side) VALUES ({endA}, {conductor}, 'A'), ({endB}, {conductor}, 'B')");
        var circuit = await Scalar<long>($"INSERT INTO circuit (code, layer, a_terminal_id, b_terminal_id) VALUES ('FYS-{tag}', 'physical', {endA}, {endB}) RETURNING id");
        await Exec($"INSERT INTO circuit_hop (circuit_id, seq, terminal_id) VALUES ({circuit}, 0, {endA}), ({circuit}, 1, {endB})");
        var service = await Scalar<long>($"INSERT INTO service (code, name, service_type) VALUES ('TJ-{tag}', 'Agenttest', 'ethernet') RETURNING id");
        await Exec($"INSERT INTO service_circuit (service_id, circuit_id) VALUES ({service}, {circuit})");
        await factory.RefreshScopesAsync();
        await factory.GraphCaughtUpAsync();
        return new TestNetwork(siteA, codeA, siteB, cableCode, service, serial);
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

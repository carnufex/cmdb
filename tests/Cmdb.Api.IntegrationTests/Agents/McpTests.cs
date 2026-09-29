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

        tools.Select(t => t.Name).Order().ShouldBe(["describe_catalog", "find_sites", "get_object", "impact", "neighbourhood", "search"]);
        tools.ShouldAllBe(t => t.ProtocolTool.Annotations!.ReadOnlyHint == true);
        resources.Select(r => r.Uri).ShouldBe(["cmdb://docs/domanmodell", "cmdb://docs/plan", "cmdb://docs/arkitektur"], ignoreOrder: true);
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
        impact.GetProperty("affectedServices")[0].GetProperty("ref").GetString().ShouldBe($"service:{net.Service}");

        var catalog = await CallAsync(client, "describe_catalog", []);
        catalog.GetProperty("categories").EnumerateArray().ShouldContain(c => c.GetProperty("key").GetString() == "radio");
    }

    [Theory]
    [InlineData("search", """{ "query": "ab" }""", "three characters")]
    [InlineData("get_object", """{ "reference": "toaster:1" }""", "Unknown type")]
    [InlineData("get_object", """{ "reference": "site:999999999" }""", "No site")]
    [InlineData("impact", """{ "reference": "service:1" }""", "cables and sites")]
    [InlineData("find_sites", """{ "equipment": [ { "attribute": { "key": "noSuchKey", "op": "eq", "value": 1 } } ] }""", "noSuchKey")]
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

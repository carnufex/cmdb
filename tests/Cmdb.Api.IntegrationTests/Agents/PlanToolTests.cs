using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Agents;

/// <summary>
/// Agents propose changes as plans (#64, ADR-0011): the plan tools over MCP with an agent token, and that only a person
/// can bring an agent's plan into production.
/// </summary>
public sealed class PlanToolTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AgentToken(string user = "cmdb-agent-demo") =>
        ApiFactory.Token(user, ["cmdb-agents"], audience: "cmdb-agents", issuer: "https://idp.test/application/o/cmdb-agents/");

    [Fact]
    public async Task An_agent_proposes_a_port_range_as_a_plan_that_only_a_person_can_apply()
    {
        var (db, api) = await NetworkAsync(31);
        await using var dbScope = db;
        await using var apiScope = api;
        var (from, to) = await TwoEquipmentWithFreePortsAsync(db, 4);
        await using var agent = await ConnectAsync(api, AgentToken());

        var plan = Json(await agent.CallToolAsync("create_plan", new Dictionary<string, object?>
        {
            ["name"] = "Patcha fyra portar",
            ["description"] = "Ny transmissionslänk.",
        }, cancellationToken: Ct));
        var reference = plan.GetProperty("ref").GetString()!;
        plan.GetProperty("createdVia").GetString().ShouldBe("mcp");
        plan.GetProperty("url").GetString().ShouldEndWith($"/?plan={reference["plan:".Length..]}");

        var added = Json(await agent.CallToolAsync("connect_ports", new Dictionary<string, object?>
        {
            ["plan"] = reference,
            ["from"] = $"equipment:{from}",
            ["fromPort"] = "1",
            ["to"] = $"equipment:{to}",
            ["toPort"] = "1",
            ["count"] = 4,
        }, cancellationToken: Ct));
        added.GetProperty("added").GetArrayLength().ShouldBe(4);

        var preview = Json(await agent.CallToolAsync("preview_plan", new Dictionary<string, object?> { ["plan"] = reference }, cancellationToken: Ct));
        preview.GetProperty("changes").GetArrayLength().ShouldBe(4);
        preview.GetProperty("problems").GetInt32().ShouldBe(0);
        preview.GetProperty("readyToApply").GetBoolean().ShouldBeTrue();

        var list = await agent.CallToolAsync("list_plans", new Dictionary<string, object?>(), cancellationToken: Ct);
        Text(list).ShouldContain("cmdb-agent-demo");

        // The agent cannot apply its plan, not even over REST; a person can.
        var id = long.Parse(reference["plan:".Length..], System.Globalization.CultureInfo.InvariantCulture);
        using var agentHttp = api.CreateClient();
        agentHttp.DefaultRequestHeaders.Authorization = new("Bearer", AgentToken());
        (await agentHttp.PostAsync($"/api/plans/{id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var person = NetworkFixture.Client(api);
        var applied = await person.PostAsync($"/api/plans/{id}/apply", null, Ct);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        (await applied.Content.ReadFromJsonAsync<ApplyResult>(Ct))!.Plan.CreatedBy.ShouldBe("cmdb-agent-demo");
    }

    [Fact]
    public async Task Changes_are_checked_and_readers_cannot_propose()
    {
        var (db, api) = await NetworkAsync(32);
        await using var dbScope = db;
        await using var apiScope = api;
        var (from, to) = await TwoEquipmentWithFreePortsAsync(db, 2);
        await using var agent = await ConnectAsync(api, AgentToken());
        var reference = Json(await agent.CallToolAsync("create_plan", new Dictionary<string, object?> { ["name"] = "Kontroller" }, cancellationToken: Ct))
            .GetProperty("ref").GetString()!;

        // The second connect of the same pair does not fit the plan's view.
        var args = new Dictionary<string, object?>
        {
            ["plan"] = reference,
            ["from"] = $"equipment:{from}",
            ["fromPort"] = "1",
            ["to"] = $"equipment:{to}",
            ["toPort"] = "1",
            ["count"] = 1,
        };
        Json(await agent.CallToolAsync("connect_ports", args, cancellationToken: Ct));
        var again = Json(await agent.CallToolAsync("connect_ports", args, cancellationToken: Ct));
        again.GetProperty("added")[0].GetProperty("problem").GetString().ShouldBe("Terminalerna är redan kopplade.");

        var bad = await agent.CallToolAsync("add_to_plan", new Dictionary<string, object?>
        {
            ["plan"] = reference,
            ["operations"] = new[] { new { kind = "connect", a = 999_999_999L, b = 1L } },
        }, cancellationToken: Ct);
        bad.IsError.ShouldBe(true);
        Text(bad).ShouldContain("finns inte");
        var tooMany = await agent.CallToolAsync("connect_ports", new Dictionary<string, object?>(args) { ["count"] = 500 }, cancellationToken: Ct);
        tooMany.IsError.ShouldBe(true);

        await using var reader = await ConnectAsync(api, ApiFactory.Token("cmdb-demo-region", ["cmdb-region-nord"]));
        var refused = await reader.CallToolAsync("create_plan", new Dictionary<string, object?> { ["name"] = "Nej" }, cancellationToken: Ct);
        refused.IsError.ShouldBe(true);
        Text(refused).ShouldContain("may read but not propose");
    }

    [Fact]
    public async Task An_agent_plans_a_new_site_with_equipment_and_patches_to_it()
    {
        var (db, api) = await NetworkAsync(33);
        await using var dbScope = db;
        await using var apiScope = api;
        var (from, _) = await TwoEquipmentWithFreePortsAsync(db, 2);
        await using var agent = await ConnectAsync(api, AgentToken());
        var plan = Json(await agent.CallToolAsync("create_plan", new Dictionary<string, object?> { ["name"] = "Ny nod" }, cancellationToken: Ct))
            .GetProperty("ref").GetString()!;

        var site = Json(await agent.CallToolAsync("add_to_plan", new Dictionary<string, object?>
        {
            ["plan"] = plan,
            ["operations"] = new[] { new { kind = "create_site", code = "NOD-AGENT-1", name = "Agentens nod", siteType = "cabinet", x = 600000.0, y = 7000000.0 } },
        }, cancellationToken: Ct)).GetProperty("added")[0].GetProperty("target").GetString()!;
        site.ShouldStartWith("site:-");
        var equipment = Json(await agent.CallToolAsync("add_to_plan", new Dictionary<string, object?>
        {
            ["plan"] = plan,
            ["operations"] = new[] { new { kind = "create_equipment", site, typeKey = "acme-ax-24", name = "NOD-AGENT-1 AX-24 1" } },
        }, cancellationToken: Ct)).GetProperty("added")[0].GetProperty("target").GetString()!;
        equipment.ShouldStartWith("equipment:-");

        var patched = Json(await agent.CallToolAsync("connect_ports", new Dictionary<string, object?>
        {
            ["plan"] = plan,
            ["from"] = equipment,
            ["fromPort"] = "ge-0/0/1",
            ["to"] = $"equipment:{from}",
            ["toPort"] = "1",
            ["count"] = 2,
        }, cancellationToken: Ct));
        patched.GetProperty("added").GetArrayLength().ShouldBe(2);
        patched.GetProperty("added")[0].GetProperty("summary").GetString()!.ShouldContain("(planerad)");
        var preview = Json(await agent.CallToolAsync("preview_plan", new Dictionary<string, object?> { ["plan"] = plan }, cancellationToken: Ct));
        preview.GetProperty("problems").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task An_agent_builds_two_sites_from_templates_and_terminates_a_cable_between_them()
    {
        var (db, api) = await NetworkAsync(34);
        await using var dbScope = db;
        await using var apiScope = api;
        await using var agent = await ConnectAsync(api, AgentToken());
        var plan = Json(await agent.CallToolAsync("create_plan", new Dictionary<string, object?> { ["name"] = "Två skåp" }, cancellationToken: Ct))
            .GetProperty("ref").GetString()!;

        async Task<string> Site(string code, double x) => Json(await agent.CallToolAsync("create_site_from_template", new Dictionary<string, object?>
        {
            ["plan"] = plan,
            ["template"] = "skap-access",
            ["code"] = code,
            ["name"] = code,
            ["x"] = x,
            ["y"] = 7_000_000.0,
        }, cancellationToken: Ct)).GetProperty("added")[0].GetProperty("target").GetString()!;
        var a = await Site("SKP-AGENT-1", 600_000);
        var b = await Site("SKP-AGENT-2", 601_000);
        var cable = Json(await agent.CallToolAsync("add_to_plan", new Dictionary<string, object?>
        {
            ["plan"] = plan,
            ["operations"] = new[] { new { kind = "create_cable", aSite = a, bSite = b, typeKey = "fiber-12" } },
        }, cancellationToken: Ct)).GetProperty("added")[0].GetProperty("target").GetString()!;

        var terminated = Json(await agent.CallToolAsync("terminate_cable", new Dictionary<string, object?> { ["plan"] = plan, ["cable"] = cable }, cancellationToken: Ct));
        terminated.GetProperty("added").GetArrayLength().ShouldBe(24);
        (await agent.CallToolAsync("terminate_cable", new Dictionary<string, object?> { ["plan"] = plan, ["cable"] = cable }, cancellationToken: Ct))
            .IsError.ShouldBe(true);
        var preview = Json(await agent.CallToolAsync("preview_plan", new Dictionary<string, object?> { ["plan"] = plan }, cancellationToken: Ct));
        preview.GetProperty("problems").GetInt32().ShouldBe(0);
        preview.GetProperty("readyToApply").GetBoolean().ShouldBeTrue();
    }

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> NetworkAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Embedded), reset: false, TextWriter.Null, ct: Ct);
        await using (var clear = db.CreateCommand("DELETE FROM reservation; DELETE FROM plan_operation; DELETE FROM plan_dependency; DELETE FROM plan"))
        {
            await clear.ExecuteNonQueryAsync(Ct);
        }
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (db, api);
    }

    [Fact]
    public async Task An_agent_imports_sites_and_cables_into_a_plan_all_or_nothing_and_cannot_apply_them()
    {
        var (db, api) = await NetworkAsync(34);
        await using var dbScope = db;
        await using var apiScope = api;
        await using var agent = await ConnectAsync(api, AgentToken());
        var plan = Json(await agent.CallToolAsync("create_plan", new Dictionary<string, object?> { ["name"] = "Utrullning" }, cancellationToken: Ct))
            .GetProperty("ref").GetString()!;
        const string csv = """
            kind;code;name;template;siteType;lat;lon;a;b;cableType
            site;AGT-RAD-1;Agentens radiosite;radiosite-standard;;63.8250;20.2630;;;
            site;AGT-SKP-1;Agentens skåp;;cabinet;63.8330;20.2840;;;
            cable;;;;;;;AGT-RAD-1;AGT-SKP-1;fiber-12
            """;

        // A dry run counts and writes nothing.
        var dry = Json(await agent.CallToolAsync("import_to_plan",
            new Dictionary<string, object?> { ["plan"] = plan, ["content"] = csv, ["dryRun"] = true }, cancellationToken: Ct));
        (dry.GetProperty("sites").GetInt32(), dry.GetProperty("cables").GetInt32(), dry.GetProperty("errors").GetArrayLength()).ShouldBe((2, 1, 0));

        var done = Json(await agent.CallToolAsync("import_to_plan",
            new Dictionary<string, object?> { ["plan"] = plan, ["content"] = csv }, cancellationToken: Ct));
        (done.GetProperty("sites").GetInt32(), done.GetProperty("cables").GetInt32()).ShouldBe((2, 1));

        // A bad row is reported with its row number and nothing from the file goes in.
        var bad = Json(await agent.CallToolAsync("import_to_plan", new Dictionary<string, object?>
        {
            ["plan"] = plan,
            ["content"] = csv.Replace("AGT-RAD-1", "AGT-RAD-2").Replace("radiosite-standard", "finns-inte"),
        }, cancellationToken: Ct));
        bad.GetProperty("errors").GetArrayLength().ShouldBeGreaterThan(0);
        bad.GetProperty("sites").GetInt32().ShouldBe(0);

        // An agent proposes and a person applies.
        using var http = NetworkFixture.Client(api, "cmdb-agent-demo", ["cmdb-agents"]);
        (await http.PostAsync($"/api/plans/{plan["plan:".Length..]}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_agent_gets_routes_over_cables_with_free_fibres_or_a_new_cable_and_puts_one_in_a_plan()
    {
        var (db, api) = await NetworkAsync(35);
        await using var dbScope = db;
        await using var apiScope = api;
        await using var agent = await ConnectAsync(api, AgentToken());
        // Two sites with a site between them, over cables that still have fibres nothing is spliced to.
        await using var cmd = db.CreateCommand("""
            WITH fr AS (SELECT k.cable_id FROM conductor k WHERE NOT EXISTS (
                            SELECT 1 FROM conductor_end ce JOIN connection x ON x.valid_to IS NULL AND (x.a_terminal_id = ce.terminal_id OR x.b_terminal_id = ce.terminal_id)
                            WHERE ce.conductor_id = k.id) GROUP BY k.cable_id),
                 e AS (SELECT c.a_site_id s, c.b_site_id t FROM cable c JOIN fr ON fr.cable_id = c.id WHERE c.lifecycle = 'in_service'
                       UNION ALL SELECT c.b_site_id, c.a_site_id FROM cable c JOIN fr ON fr.cable_id = c.id WHERE c.lifecycle = 'in_service')
            SELECT e1.s, e2.t FROM e e1 JOIN e e2 ON e1.t = e2.s WHERE e1.s <> e2.t
            ORDER BY e1.s, e2.t LIMIT 1
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        var (from, to) = (reader.GetInt64(0), reader.GetInt64(1));
        await reader.CloseAsync();

        var suggestion = Json(await agent.CallToolAsync("suggest_route",
            new Dictionary<string, object?> { ["from"] = $"site:{from}", ["to"] = $"site:{to}" }, cancellationToken: Ct));
        var alternatives = suggestion.GetProperty("alternatives");
        alternatives.GetArrayLength().ShouldBeGreaterThan(0);
        var best = alternatives[0];
        best.GetProperty("sites")[0].GetProperty("id").GetInt64().ShouldBe(from);
        best.GetProperty("sites").EnumerateArray().Last().GetProperty("id").GetInt64().ShouldBe(to);
        best.GetProperty("splices").GetInt32().ShouldBe(best.GetProperty("cables").GetArrayLength() - 1);
        best.GetProperty("cables").EnumerateArray().ShouldAllBe(c => c.GetProperty("free").GetInt32() >= 1);
        suggestion.GetProperty("elapsedMs").GetDouble().ShouldBeLessThan(2000);

        // Into a plan: one splice per site on the way and fibre.
        var plan = Json(await agent.CallToolAsync("create_plan", new Dictionary<string, object?> { ["name"] = "Ny förbindelse" }, cancellationToken: Ct))
            .GetProperty("ref").GetString()!;
        var added = Json(await agent.CallToolAsync("add_route_to_plan",
            new Dictionary<string, object?> { ["plan"] = plan, ["from"] = $"site:{from}", ["to"] = $"site:{to}" }, cancellationToken: Ct));
        added.GetProperty("added").GetArrayLength().ShouldBe(best.GetProperty("splices").GetInt32());
        added.GetProperty("added").EnumerateArray().ShouldAllBe(a => a.GetProperty("kind").GetString() == "connect");

        // What the plan now uses counts as taken, so the same route is no longer the best one.
        var again = Json(await agent.CallToolAsync("suggest_route",
            new Dictionary<string, object?> { ["from"] = $"site:{from}", ["to"] = $"site:{to}", ["plan"] = plan }, cancellationToken: Ct));
        again.GetProperty("alternatives").GetArrayLength().ShouldBeGreaterThan(0);

        // More fibres than any cable has free: a new cable between the closest sites of the two sides, with its splices.
        var wide = Json(await agent.CallToolAsync("suggest_route",
            new Dictionary<string, object?> { ["from"] = $"site:{from}", ["to"] = $"site:{to}", ["fibres"] = 96 }, cancellationToken: Ct));
        var newCable = wide.GetProperty("alternatives")[0].GetProperty("newCable");
        newCable.GetProperty("typeKey").GetString().ShouldBe("fiber-96");
        wide.GetProperty("note").GetString()!.ShouldContain("ny kabel");

        // The tool refuses what it cannot do.
        (await agent.CallToolAsync("suggest_route", new Dictionary<string, object?> { ["from"] = $"site:{from}", ["to"] = $"site:{from}" },
            cancellationToken: Ct)).IsError.ShouldBe(true);
    }

    [Fact]
    public async Task An_agent_classifies_new_equipment_in_a_plan_and_checks_what_that_does_to_the_site()
    {
        var (db, api) = await NetworkAsync(32);
        await using var dbScope = db;
        await using var apiScope = api;
        await using var agent = await ConnectAsync(api, AgentToken());
        await using var cmd = db.CreateCommand("""
            SELECT s.id FROM site s
            WHERE s.lifecycle = 'in_service' AND EXISTS (SELECT 1 FROM location l WHERE l.site_id = s.id AND l.kind = 'rack')
              AND (SELECT count(*) FROM cable c WHERE (c.a_site_id = s.id OR c.b_site_id = s.id) AND c.lifecycle = 'in_service') = 1
            ORDER BY s.id LIMIT 1
            """);
        var site = (long)(await cmd.ExecuteScalarAsync(Ct))!;

        var plan = Json(await agent.CallToolAsync("create_plan", new Dictionary<string, object?> { ["name"] = "Ny kritisk switch" }, cancellationToken: Ct))
            .GetProperty("ref").GetString()!;
        var created = Json(await agent.CallToolAsync("add_to_plan", new Dictionary<string, object?>
        {
            ["plan"] = plan,
            ["operations"] = new[] { new Dictionary<string, object?> { ["kind"] = "create_equipment", ["site"] = $"site:{site}", ["typeKey"] = "acme-ax-24", ["name"] = "AGENT-SW-1" } },
        }, cancellationToken: Ct));
        var reference = created.GetProperty("added")[0].GetProperty("target").GetString()!;
        reference.ShouldStartWith("equipment:-");

        // A planned object is named by its negative reference, like a planned site in create_equipment.
        var classified = Json(await agent.CallToolAsync("add_to_plan", new Dictionary<string, object?>
        {
            ["plan"] = plan,
            ["operations"] = new[]
            {
                new Dictionary<string, object?> { ["kind"] = "set_classification", ["target"] = reference, ["schema"] = "criticality", ["level"] = 5 },
            },
        }, cancellationToken: Ct));
        classified.GetProperty("added")[0].GetProperty("summary").GetString()!.ShouldContain("AGENT-SW-1");

        var report = Json(await agent.CallToolAsync("check_plan_classification", new Dictionary<string, object?> { ["plan"] = plan }, cancellationToken: Ct));
        var finding = report.GetProperty("findings")[0];
        finding.GetProperty("after").GetInt32().ShouldBe(5);
        finding.GetProperty("unmet").GetArrayLength().ShouldBe(2);
        report.GetProperty("introduced").GetInt32().ShouldBe(2);

        // An agent cannot apply it, whatever the requirements say.
        using var http = NetworkFixture.Client(api, "cmdb-agent-demo", ["cmdb-agents"]);
        (await http.PostAsync($"/api/plans/{plan["plan:".Length..]}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> api, string token)
    {
        var http = api.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    /// <summary>Two pieces of equipment whose first <paramref name="ports"/> ports are all unconnected.</summary>
    private static async Task<(long From, long To)> TwoEquipmentWithFreePortsAsync(NpgsqlDataSource db, int ports)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT e.id FROM equipment e
            WHERE (SELECT count(*) FROM port p WHERE p.equipment_id = e.id) >= {ports}
              AND NOT EXISTS (
                  SELECT 1 FROM (SELECT p.terminal_id FROM port p WHERE p.equipment_id = e.id ORDER BY p.position LIMIT {ports}) f
                  JOIN connection c ON f.terminal_id IN (c.a_terminal_id, c.b_terminal_id))
            ORDER BY e.id LIMIT 2
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return (ids[0], ids[1]);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static JsonElement Json(CallToolResult result)
    {
        result.IsError.ShouldNotBe(true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement;
    }
}

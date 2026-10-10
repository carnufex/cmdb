using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Catalog;
using Cmdb.Cli;
using Cmdb.DataGen.Exchange;
using Cmdb.Graph;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Reconciliation;

/// <summary>
/// <c>cmdb sync</c> (#217): the reference adapter reads a fake ACME Monitor page by page, and reconciliation runs with the
/// integration's own account and scope (ADR-0021).
/// </summary>
public sealed class SyncTests(ApiFactory factory) : IAsyncLifetime
{
    private static readonly string[] Integration = ["cmdb-integration", "cmdb-integration-acme-monitor"];

    private readonly string _folder = Directory.CreateTempSubdirectory("cmdb-sync-test-").FullName;
    private WebApplication? _monitor;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Example => Path.Combine(AppContext.BaseDirectory, "exempel", "import");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_monitor is not null)
        {
            await _monitor.DisposeAsync();
        }
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task The_reference_adapter_is_reconciled_inside_the_integrations_scope()
    {
        await using var db = await ImportedAsync();
        await using var api = await ApiAsync(db);

        var dry = await SyncAsync(api, Integration, "sync", "acme-monitor", "--dry-run", "--json");
        dry.Code.ShouldBe(ExitCodes.Ok, dry.Error);
        var dryReport = JsonNode.Parse(dry.Output)!;
        dryReport["reviewPlanId"].ShouldBeNull();
        (await Scalar(db, "SELECT count(*) FROM plan")).ShouldBe(0);
        // Paged: the second page of sites was read too, and the device with an unknown model was left out with a warning.
        Count(dryReport, "site")["reported"]!.GetValue<int>().ShouldBe(3);
        dry.Error.ShouldContain("modellen 'XR-1' saknas");

        var run = await SyncAsync(api, Integration, "sync", "acme-monitor");
        run.Code.ShouldBe(ExitCodes.Ok, run.Error);
        run.Output.ShouldStartWith("Avstämning ");
        run.Output.ShouldContain("mot acme-monitor");

        var id = await Scalar(db, "SELECT max(id) FROM reconciliation");
        var report = JsonNode.Parse((await SyncAsync(api, Integration, "reconciliation", $"{id}", "--json")).Output)!;
        // Sites by code and equipment by serial number: linked to the monitor's own ids.
        Count(report, "site")["linked"]!.GetValue<int>().ShouldBe(2);
        Count(report, "equipment")["linked"]!.GetValue<int>().ShouldBe(2);
        // The southern site is outside Övervakning Nord: it is not created, and names nothing in cmdb.
        report["reasons"]!["outside-scope"]!.GetValue<int>().ShouldBe(1);
        var south = report["deviations"]!.AsArray().Single(d => d!["externalId"]!.GetValue<string>() == "m-s3")!;
        (south["reason"]!.GetValue<string>(), south["objectId"]).ShouldBe(("outside-scope", null));
        (await Scalar(db, "SELECT count(*) FROM site WHERE code = 'EX-SYD-1'")).ShouldBe(0);
        // The monitor is not trusted with firmware, so it is reported, not changed.
        report["reasons"]!["not-allowed"]!.GetValue<int>().ShouldBeGreaterThan(0);
        (await Text(db, "SELECT attributes->>'firmware' FROM equipment WHERE external_id = 'ex-e1'")).ShouldBeNull();
        // The new device goes to review, in the rack the monitor names.
        var plan = report["reviewPlanId"]!.GetValue<long>();
        (await Text(db, $"SELECT string_agg(kind || ':' || coalesce(payload->>'rack', payload->>'name'), ',' ORDER BY seq) FROM plan_operation WHERE plan_id = {plan}"))
            !.ShouldContain("create_equipment:Rack 1");
        (await Text(db, $"SELECT created_by FROM plan WHERE id = {plan}")).ShouldBe("integration-acme-monitor");

        var listed = await SyncAsync(api, Integration, "reconciliations");
        listed.Output.ShouldContain($"reconciliation:{id}  acme-monitor");

        // An agent with the whole network reads the report; a person in another scope does not see the run.
        await using (var agent = await McpAsync(api, ApiFactory.Token("cmdb-agent-demo", ["cmdb-agents"], audience: "cmdb-agents",
                         issuer: "https://idp.test/application/o/cmdb-agents/")))
        {
            var result = await agent.CallToolAsync("get_reconciliation", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: Ct);
            result.IsError.ShouldNotBe(true);
            JsonNode.Parse(((TextContentBlock)result.Content[0]).Text)!["reviewPlanId"]!.GetValue<long>().ShouldBe(plan);
        }
        using var region = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await region.GetAsync($"/api/reconciliations/{id}", Ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    private static async Task<McpClient> McpAsync(WebApplicationFactory<Program> api, string token)
    {
        var http = api.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    [Fact]
    public async Task Without_the_integration_role_or_its_secret_nothing_is_sent()
    {
        await using var db = await ImportedAsync();
        await using var api = await ApiAsync(db);

        var forbidden = await SyncAsync(api, ["cmdb-integration-acme-monitor"], "sync", "acme-monitor");
        forbidden.Code.ShouldBe(ExitCodes.Auth, forbidden.Error);

        var noSecret = await SyncAsync(api, Integration, ["sync", "acme-monitor"], withToken: false);
        noSecret.Code.ShouldBe(ExitCodes.Failed);
        noSecret.Error.ShouldContain("CMDB_SYNC_ACME_MONITOR_TOKEN");
        (await Scalar(db, "SELECT count(*) FROM reconciliation")).ShouldBe(0);

        var saved = await SyncAsync(api, Integration, "sync", "acme-monitor", "--out", Path.Combine(_folder, "out"));
        saved.Code.ShouldBe(ExitCodes.Ok, saved.Error);
        File.ReadAllLines(Path.Combine(_folder, "out", "equipment.csv")).Length.ShouldBe(4);
        File.ReadAllText(Path.Combine(_folder, "out", "locations.csv")).ShouldContain("m-s1/Rack 1,m-s1,,rack,Rack 1,");
        (await Scalar(db, "SELECT count(*) FROM reconciliation")).ShouldBe(0);
    }

    private Task<(int Code, string Output, string Error)> SyncAsync(WebApplicationFactory<Program> api, string[] groups, params string[] args) =>
        SyncAsync(api, groups, args, withToken: true);

    private async Task<(int Code, string Output, string Error)> SyncAsync(WebApplicationFactory<Program> api, string[] groups, string[] args,
        bool withToken)
    {
        using var http = api.CreateClient();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var config = Path.Combine(_folder, "acme-monitor.json");
        await File.WriteAllTextAsync(config, """{ "models": { "CR-8": "acme-cr-8", "AX-24": "acme-ax-24" } }""", Ct);
        var env = new Dictionary<string, string?>
        {
            ["CMDB_TOKEN"] = ApiFactory.Token("integration-acme-monitor", groups),
            ["CMDB_SYNC_ACME_MONITOR_URL"] = "http://monitor.test/",
            ["CMDB_SYNC_ACME_MONITOR_TOKEN"] = withToken ? "monitor-secret" : null,
            ["CMDB_SYNC_CONFIG"] = config,
        };
        var code = await CliApp.RunAsync([.. args, "--url", http.BaseAddress!.ToString()], output, error, http, env, await MonitorAsync(), Ct);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>A fake ACME Monitor: two pages of sites, one of devices, and a bearer token.</summary>
    private async Task<HttpClient> MonitorAsync()
    {
        if (_monitor is null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            _monitor = builder.Build();
            _monitor.Use(async (context, next) =>
            {
                if (context.Request.Headers.Authorization != "Bearer monitor-secret")
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next(context);
            });
            _monitor.MapGet("/api/v1/sites", (int page) => Results.Text(page == 1
                ? """{ "items": [ { "id": "m-s1", "code": "EX-NAV-1", "name": "Exempelnav", "type": "hub", "lat": 63.8258, "lon": 20.2630, "state": "active" } ], "nextPage": 2 }"""
                : """
                  { "items": [
                    { "id": "m-s2", "code": "EX-RAD-1", "name": "Exempelradio", "type": "radio", "lat": 63.8400, "lon": 20.3000, "state": "active" },
                    { "id": "m-s3", "code": "EX-SYD-1", "name": "Sydnav", "type": "hub", "lat": 57.7089, "lon": 11.9746, "state": "active" } ],
                    "nextPage": null }
                  """, "application/json"));
            _monitor.MapGet("/api/v1/devices", (int page) => Results.Text("""
                { "items": [
                  { "id": "m-d1", "site": "m-s1", "rack": "Rack 1", "hostname": "EX-NAV-1 CR-8 1", "model": "CR-8", "serial": "SN-EX-0001", "firmware": "2.0.0", "state": "active" },
                  { "id": "m-d5", "site": "m-s2", "rack": "Skåp", "hostname": "EX-RAD-1 AX-24 1", "model": "AX-24", "serial": "SN-EX-0005", "state": "active" },
                  { "id": "m-d9", "site": "m-s1", "rack": "Rack 1", "hostname": "EX-NAV-1 CR-8 2", "model": "CR-8", "serial": "SN-NEW-0009", "state": "planned" },
                  { "id": "m-d10", "site": "m-s1", "rack": "Rack 1", "hostname": "okänd", "model": "XR-1", "state": "active" } ],
                  "nextPage": null }
                """, "application/json"));
            await _monitor.StartAsync(Ct);
        }
        return _monitor.GetTestClient();
    }

    private static JsonNode Count(JsonNode report, string type) =>
        report["counts"]!.AsArray().Single(c => c!["objectType"]!.GetValue<string>() == type)!;

    private async Task<NpgsqlDataSource> ImportedAsync()
    {
        var db = await factory.NewDatabaseAsync();
        (await NetworkImport.RunAsync(db, Example, "acme-nms", TypeCatalog.Current, dryRun: false, TextWriter.Null, Ct)).Errors.ShouldBeEmpty();
        return db;
    }

    private async Task<WebApplicationFactory<Program>> ApiAsync(NpgsqlDataSource db)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return api;
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> Text(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync(Ct) as string;
    }
}

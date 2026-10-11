using System.Text.Json;
using Cmdb.Cli;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Agents;

/// <summary>The cmdb CLI (#86) against the API, over the same routes as the web app and the MCP tools.</summary>
public sealed class CliTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => await RunAsAsync(ApiFactory.Token(), args);

    private async Task<(int Code, string Output, string Error)> RunAsAsync(string? token, params string[] args)
    {
        var (_, api) = await NetworkFixture.GetAsync(factory);
        using var http = api.CreateClient();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var env = new Dictionary<string, string?> { ["CMDB_TOKEN"] = token };
        var code = await CliApp.RunAsync([.. args, "--url", http.BaseAddress!.ToString()], output, error, http, env, ct: Ct);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Search_and_get_print_references_with_links_and_json_is_the_api_answer()
    {
        var (db, _) = await NetworkFixture.GetAsync(factory);
        var code = await Text(db, "SELECT code FROM site WHERE site_type = 'hub' ORDER BY id LIMIT 1");
        var id = await Scalar(db, $"SELECT id FROM site WHERE code = '{code}'");

        var search = await RunAsync("search", code, "--limit", "5");
        search.Code.ShouldBe(ExitCodes.Ok, search.Error);
        search.Output.ShouldContain($"site:{id}  {code}");
        search.Output.ShouldContain($"/?p=site:{id}");

        var get = await RunAsync("get", code);
        get.Output.ShouldStartWith($"site:{id}  {code}");
        get.Output.ShouldContain("cables:");

        var json = await RunAsync("get", $"site:{id}", "--json");
        JsonDocument.Parse(json.Output).RootElement.GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Impact_trace_find_sites_catalog_and_plans_answer_like_the_mcp_tools()
    {
        var (db, _) = await NetworkFixture.GetAsync(factory);
        var cable = await Scalar(db, "SELECT c.id FROM cable c JOIN conductor k ON k.cable_id = c.id JOIN conductor_end ce ON ce.conductor_id = k.id JOIN circuit_hop h ON h.terminal_id = ce.terminal_id ORDER BY c.id LIMIT 1");
        var service = await Scalar(db, "SELECT min(service_id) FROM service_circuit");

        var impact = await RunAsync("impact", $"cable:{cable}");
        impact.Code.ShouldBe(ExitCodes.Ok, impact.Error);
        impact.Output.ShouldStartWith($"cable:{cable}: ");
        impact.Output.ShouldContain("kretsar");

        var trace = await RunAsync("trace", $"service:{service}");
        trace.Output.ShouldStartWith($"service:{service}");

        var sites = await RunAsync("find-sites", "--type", "hub", "--limit", "3");
        sites.Output.Split('\n')[0].TrimEnd().ShouldEndWith("siter");
        sites.Output.ShouldContain("(hub,");

        (await RunAsync("catalog")).Output.ShouldContain("acme-ax-24");
        (await RunAsync("plans")).Code.ShouldBe(ExitCodes.Ok);
        (await RunAsync("whoami")).Output.ShouldContain("Omfång: Hela nätet");
    }

    [Fact]
    public async Task Exit_codes_tell_usage_not_found_and_auth_apart()
    {
        (await RunAsync()).Code.ShouldBe(ExitCodes.Usage);
        (await RunAsync("frobnicate")).Code.ShouldBe(ExitCodes.Usage);
        (await RunAsync("trace")).Code.ShouldBe(ExitCodes.Usage);
        (await RunAsync("search", "x", "--limit", "999")).Code.ShouldBe(ExitCodes.Usage);
        var missing = await RunAsync("get", "site:999999999");
        missing.Code.ShouldBe(ExitCodes.NotFound);
        missing.Error.ShouldContain("Finns inte");
        (await RunAsync("get", "NO-SUCH-CODE-42")).Code.ShouldBe(ExitCodes.NotFound);

        // Nobody: an anonymous call; the region user: outside the scope looks like missing.
        (await RunAsAsync("not-a-token", "whoami")).Code.ShouldBe(ExitCodes.Auth);
        var (db, _) = await NetworkFixture.GetAsync(factory);
        var south = await Scalar(db, "SELECT s.id FROM site s WHERE ST_Y(ST_PointOnSurface(s.geom)) < 6400000 ORDER BY s.id LIMIT 1");
        (await RunAsAsync(ApiFactory.Token("cmdb-demo-region", ["cmdb-region-nord"]), "get", $"site:{south}")).Code.ShouldBe(ExitCodes.NotFound);
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> Text(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

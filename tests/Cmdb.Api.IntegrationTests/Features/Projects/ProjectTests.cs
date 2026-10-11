using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Api.Features.Projects;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Projects;

/// <summary>
/// Projects (#293, ADR-0021): an area with stages that are plans building on each other, their changes, cable and cost,
/// what to do next, and who sees the project.
/// </summary>
public sealed class ProjectTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_project_holds_an_area_and_its_stages_with_their_changes_cost_and_next_step()
    {
        var (db, api) = await ApiAsync(64);
        await using var _ = api;
        using var client = NetworkFixture.Client(api);
        var centre = await Row(db, "SELECT ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)) FROM site WHERE lifecycle = 'in_service' ORDER BY id LIMIT 1");
        var (x, y) = ((double)centre[0]!, (double)centre[1]!);
        double[][] Square(double r) => [[x - r, y - r], [x + r, y - r], [x + r, y + r], [x - r, y + r]];

        var created = await client.PostAsJsonAsync("/api/projects", new { name = "Testområdet", description = "Fiber till byn", area = Square(2000) }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        var project = (await created.Content.ReadFromJsonAsync<ProjectDetail>(Ct))!;
        project.Stages.ShouldBeEmpty();
        project.NextStep.ShouldBe("Lägg till den första etappen.");
        project.Area!.ShouldHaveSingleItem().Length.ShouldBe(5);
        (await client.PostAsJsonAsync("/api/projects", new { name = "Testområdet", area = Square(100) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/projects", new { name = "Korsat", area = new[] { new[] { x, y }, new[] { x + 10, y + 10 }, new[] { x + 10, y }, new[] { x, y + 10 } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Two stages: the second builds on the first.
        project = await StageAsync(client, project.Id, "");
        project = await StageAsync(client, project.Id, "Etapp två");
        project.Stages.Select(s => s.Plan.Name).ShouldBe(["Testområdet, etapp 1", "Etapp två"]);
        project.Stages[1].Plan.DependsOn.ShouldBe([project.Stages[0].Plan.Id]);
        project.NextStep.ShouldBe("Rita Testområdet, etapp 1: etappen har inga ändringar än.");

        // A cable in the first stage: its length and price show on the stage and in the totals.
        var first = project.Stages[0].Plan.Id;
        var sites = await Row(db, "SELECT min(id), max(id) FROM (SELECT id FROM site WHERE lifecycle = 'in_service' ORDER BY id LIMIT 2) s");
        var cableType = TypeCatalog.Current.CableTypes.First(t => t.CostPerMeter is not null).Key;
        (await client.PostAsJsonAsync($"/api/plans/{first}/operations", new { kind = "create_cable", aSiteId = sites[0], bSiteId = sites[1], typeKey = cableType }, Ct))
            .IsSuccessStatusCode.ShouldBeTrue();
        project = (await client.GetFromJsonAsync<ProjectDetail>($"/api/projects/{project.Id}", Ct))!;
        project.Stages[0].Changes.ShouldBe(1);
        project.Stages[0].CableMetres.ShouldBeGreaterThan(0);
        project.Cost.ShouldBe(project.Stages[0].Cost);
        project.Cost.ShouldBeGreaterThan(0);
        project.NextStep.ShouldBe("Granska och för in Testområdet, etapp 1.");

        // Applying the first stage moves the next step on; the order can be changed and existing plans taken in.
        (await client.PostAsync($"/api/plans/{first}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        project = (await client.GetFromJsonAsync<ProjectDetail>($"/api/projects/{project.Id}", Ct))!;
        project.Stages[0].Plan.Status.ShouldBe("applied");
        project.NextStep.ShouldBe("Rita Etapp två: etappen har inga ändringar än.");
        var loose = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Lös plan" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt64();
        var reordered = await client.PutAsJsonAsync($"/api/projects/{project.Id}/stages", new { plans = new[] { project.Stages[0].Plan.Id, loose, project.Stages[1].Plan.Id } }, Ct);
        (await reordered.Content.ReadFromJsonAsync<ProjectDetail>(Ct))!.Stages.Select(s => s.Plan.Name).ShouldBe(["Testområdet, etapp 1", "Lös plan", "Etapp två"]);

        // A plan is a stage in one project at most.
        var other = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Annat område", area = Square(500) }, Ct)).Content.ReadFromJsonAsync<ProjectDetail>(Ct))!;
        (await client.PutAsJsonAsync($"/api/projects/{other.Id}/stages", new { plans = new[] { loose } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Renamed and redrawn.
        var renamed = await client.PutAsJsonAsync($"/api/projects/{other.Id}", new { name = "Annat område 2", area = Square(800) }, Ct);
        (await renamed.Content.ReadFromJsonAsync<ProjectDetail>(Ct))!.Name.ShouldBe("Annat område 2");
        (await client.GetFromJsonAsync<List<ProjectSummary>>("/api/projects", Ct))!.Select(p => p.Name).ShouldBe(["Annat område 2", "Testområdet"]);
        (await Scalar<long>(db, "SELECT count(*) FROM operation WHERE command IN ('create_project', 'update_project', 'set_project_stages')")).ShouldBeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public async Task A_project_is_seen_by_those_whose_scopes_reach_its_area_and_by_agents()
    {
        var (db, api) = await ApiAsync(65);
        await using var _ = api;
        using var client = NetworkFixture.Client(api);
        var north = await Row(db, "SELECT ST_X(c), ST_Y(c) FROM access_scope a, LATERAL ST_PointOnSurface(a.area) c WHERE a.key = 'region-nord'");
        var south = await Row(db, """
            SELECT ST_X(ST_PointOnSurface(s.geom)), ST_Y(ST_PointOnSurface(s.geom)) FROM site s
            WHERE NOT EXISTS (SELECT 1 FROM access_scope a WHERE a.key = 'region-nord' AND ST_DWithin(a.area, s.geom, 5000)) ORDER BY s.id LIMIT 1
            """);
        double[][] Around(object?[] p) { var (x, y) = (Convert.ToDouble(p[0], CultureInfo.InvariantCulture), Convert.ToDouble(p[1], CultureInfo.InvariantCulture)); return [[x - 500, y - 500], [x + 500, y - 500], [x + 500, y + 500], [x - 500, y + 500]]; }
        var inNorth = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Norr", area = Around(north) }, Ct)).Content.ReadFromJsonAsync<ProjectDetail>(Ct))!;
        var inSouth = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Söder", area = Around(south) }, Ct)).Content.ReadFromJsonAsync<ProjectDetail>(Ct))!;

        using var regional = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await regional.GetFromJsonAsync<List<ProjectSummary>>("/api/projects", Ct))!.Select(p => p.Name).ShouldBe(["Norr"]);
        (await regional.GetAsync($"/api/projects/{inSouth.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await regional.PostAsJsonAsync("/api/projects", new { name = "Försök", area = Around(north) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var http = NetworkFixture.Client(api);
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: true),
            cancellationToken: Ct);
        var result = await mcp.CallToolAsync("get_project", new Dictionary<string, object?> { ["reference"] = $"project:{inNorth.Id}" }, cancellationToken: Ct);
        result.IsError.ShouldNotBe(true);
        string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)).ShouldContain("\"name\":\"Norr\"");
    }

    private static async Task<ProjectDetail> StageAsync(HttpClient client, long project, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/stages", new { name }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ProjectDetail>(Ct))!;
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
}

using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Api.Features.Trace;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Plans;

/// <summary>
/// Mass provisioning in plans (#26): site templates, port-to-fibre patterns, suggested termination of a new cable, and
/// batches that add all their operations or none.
/// </summary>
public sealed class PlanBatchTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_site_from_a_template_is_built_in_one_step()
    {
        var (db, api) = await NetworkAsync(51);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var template = SiteTemplates.Embedded.Find("radiosite-standard")!;
        var plan = await CreateAsync(client, "Ny radiosite");

        (await client.GetFromJsonAsync<List<TemplateSummary>>("/api/templates", Ct))!.ShouldContain(t => t.Key == "radiosite-standard");
        var ops = await PostAsync(client, $"/api/plans/{plan.Id}/templates",
            new { templateKey = "radiosite-standard", code = "RAD-MALL-1", name = "Mallsite", x = 640000.0, y = 7050000.0 });

        ops.Count.ShouldBe(1 + template.Equipment.Count + template.Connections.Count);
        ops.ShouldAllBe(o => o.Problem == null);
        var view = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        view.Problems.ShouldBe(0);
        view.Planned!.Sites.Single().Code.ShouldBe("RAD-MALL-1");

        (await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var site = await Scalar(db, "SELECT id FROM site WHERE code = 'RAD-MALL-1'");
        (await Scalar(db, $"SELECT count(*) FROM equipment WHERE site_id = {site}")).ShouldBe(template.Equipment.Count);
        (await Scalar(db, $"SELECT count(*) FROM location WHERE site_id = {site} AND kind = 'rack' AND name IN ('Rack 1', 'Mast')")).ShouldBe(2);
        (await Scalar(db, $"SELECT count(*) FROM location WHERE site_id = {site} AND kind = 'building'")).ShouldBe(1);
        (await Scalar(db, $"""
            SELECT count(*) FROM connection c JOIN port p ON p.terminal_id = c.a_terminal_id JOIN equipment e ON e.id = p.equipment_id
            WHERE c.valid_to IS NULL AND e.site_id = {site}
            """)).ShouldBe(template.Connections.Count);
    }

    [Fact]
    public async Task A_new_cable_is_terminated_as_suggested_and_patterns_follow_offset_and_step()
    {
        var (db, api) = await NetworkAsync(52);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var plan = await CreateAsync(client, "Ny fiber mellan två skåp");
        var first = await PostAsync(client, $"/api/plans/{plan.Id}/templates",
            new { templateKey = "skap-access", code = "SKP-MALL-1", name = "Skåp 1", x = 640000.0, y = 7050000.0 });
        var second = await PostAsync(client, $"/api/plans/{plan.Id}/templates",
            new { templateKey = "skap-access", code = "SKP-MALL-2", name = "Skåp 2", x = 641000.0, y = 7051000.0 });
        var cable = (await PostAsync(client, $"/api/plans/{plan.Id}/operations/batch", new
        {
            operations = new[] { new { kind = "create_cable", aSiteId = first[0].Target!.Id, bSiteId = second[0].Target!.Id, typeKey = "fiber-12" } },
        })).Single();

        var termination = (await client.GetFromJsonAsync<Termination>($"/api/plans/{plan.Id}/cables/{cable.Target!.Id}/termination", Ct))!;
        termination.Sides.Select(s => (s.Side, s.Equipment, s.Fibres)).ShouldBe([("A", "SKP-MALL-1 ODF-24 1", 12), ("B", "SKP-MALL-2 ODF-24 1", 12)]);
        foreach (var side in termination.Sides)
        {
            (await PostAsync(client, $"/api/plans/{plan.Id}/operations/batch", new { operations = side.Operations })).ShouldAllBe(o => o.Problem == null);
        }
        // The first switch's uplink now reaches the second switch through the planned fibre.
        var uplink = first.First(o => o.Kind == "connect").Terminals.Single(t => t.Label.Contains("IX-8", StringComparison.Ordinal)).TerminalId;
        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={uplink}&plan={plan.Id}", Ct))!;
        trace.Physical!.Hops.ShouldContain(h => h.Label.StartsWith("SKP-MALL-2 IX-8 1", StringComparison.Ordinal));
        // Terminated: a second suggestion finds nothing left to splice.
        (await client.GetFromJsonAsync<Termination>($"/api/plans/{plan.Id}/cables/{cable.Target.Id}/termination", Ct))!
            .Sides.ShouldAllBe(s => s.Fibres == 0);

        // Patterns: ports 13, 15, 17 of the second ODF to fibres 2, 5, 8 at the B side of a new 24-fibre cable.
        var odf = second.Single(o => o.Kind == "create_equipment" && o.Summary.Contains("ODF", StringComparison.Ordinal)).Target!.Id;
        var cable24 = (await PostAsync(client, $"/api/plans/{plan.Id}/operations/batch", new
        {
            operations = new[] { new { kind = "create_cable", aSiteId = first[0].Target!.Id, bSiteId = second[0].Target!.Id, typeKey = "fiber-24" } },
        })).Single().Target!.Id;
        var spliced = await PostAsync(client, $"/api/plans/{plan.Id}/patterns", new
        {
            equipmentId = odf,
            fromPort = "13",
            count = 3,
            portStep = 2,
            cableId = cable24,
            fromConductor = 2,
            conductorStep = 3,
            side = "B",
        });
        spliced.Select(o => o.Terminals[0].Label).ShouldBe(["SKP-MALL-2 ODF-24 1 · 13 (planerad)", "SKP-MALL-2 ODF-24 1 · 15 (planerad)",
            "SKP-MALL-2 ODF-24 1 · 17 (planerad)"]);
        spliced.Select(o => o.Terminals[1].Label).ShouldBe([$"NY-K{-cable24} ledare 2 (B, planerad)", $"NY-K{-cable24} ledare 5 (B, planerad)",
            $"NY-K{-cable24} ledare 8 (B, planerad)"]);
        var tooFar = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/patterns",
            new { equipmentId = odf, fromPort = "1", count = 10, cableId = cable24, fromConductor = 20, side = "B" }, Ct);
        tooFar.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooFar.Content.ReadAsStringAsync(Ct)).ShouldContain("bara 24 ledare");
    }

    [Fact]
    public async Task A_batch_adds_all_or_nothing()
    {
        var (db, api) = await NetworkAsync(53);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var plan = await CreateAsync(client, "Allt eller inget");

        var response = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations/batch", new
        {
            operations = new object[]
            {
                new { kind = "create_site", code = "NY-BATCH-1", name = "Ny", siteType = "radio", x = 600000.0, y = 7000000.0 },
                new { kind = "create_equipment", siteId = -999_999L, typeKey = "acme-ax-24", name = "x" },
            },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Operation 2");
        (await Scalar(db, $"SELECT count(*) FROM plan_operation WHERE plan_id = {plan.Id}")).ShouldBe(0);
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

    private static async Task<PlanSummary> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/plans", new { name }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
    }

    private static async Task<List<PlanOperationView>> PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<PlanOperationView>>(Ct))!;
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

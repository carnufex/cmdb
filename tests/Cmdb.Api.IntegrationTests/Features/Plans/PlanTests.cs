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
/// Plans (#24, ADR-0005): change sets with dependencies, viewed as production + delta in the graph, applied into
/// production, and flagging of plans that build on one that changes. Each test has its own network, since applying a
/// plan changes it.
/// </summary>
public sealed class PlanTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plan_view_is_production_plus_the_plan_and_production_is_untouched()
    {
        var (db, api) = await NetworkAsync(11);
        await using var _db = db;
        await using var _api = api;
        using var client = NetworkFixture.Client(api);
        var (a, b) = await FreePortsAsync(db);

        var plan = await CreateAsync(client, "Ny patchning");
        var op = await AddAsync(client, plan.Id, new { kind = "connect", a, b, connectionKind = "patch" });
        op.Problem.ShouldBeNull();
        op.Summary.ShouldStartWith("Koppla ");
        op.Terminals.Select(t => t.TerminalId).ShouldBe([a, b]);

        var inPlan = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={a}&plan={plan.Id}", Ct))!;
        var inProduction = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={a}", Ct))!;
        inPlan.Physical!.Hops.ShouldContain(h => h.TerminalId == b);
        inProduction.Physical!.Hops.ShouldNotContain(h => h.TerminalId == b);

        var diff = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        diff.Changes.Single().Id.ShouldBe(op.Id);
        diff.Problems.ShouldBe(0);
        diff.Sites.ShouldNotBeEmpty();
        // The budget row "switch view" (docs/plan.md) on a warm view: the first call may share the machine with other
        // test hosts loading their graphs.
        var warm = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        warm.ElapsedMs.ShouldBeLessThan(100);
        (await Count(db, $"SELECT count(*) FROM connection WHERE valid_to IS NULL AND {a} IN (a_terminal_id, b_terminal_id)")).ShouldBe(0);
    }

    [Fact]
    public async Task Dependencies_stack_in_order_and_cycles_are_refused()
    {
        var (db, api) = await NetworkAsync(12);
        await using var _db = db;
        await using var _api = api;
        using var client = NetworkFixture.Client(api);
        var (a, b) = await FreePortsAsync(db);

        var first = await CreateAsync(client, "Etapp 1");
        await AddAsync(client, first.Id, new { kind = "connect", a, b, connectionKind = "patch" });
        var second = await CreateAsync(client, "Etapp 2", first.Id);
        var undo = await AddAsync(client, second.Id, new { kind = "disconnect", a, b });
        undo.Problem.ShouldBeNull("the connection exists in the view of the plan it builds on");

        var diff = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{second.Id}/view", Ct))!;
        diff.Plans.Select(p => p.Id).ShouldBe([first.Id, second.Id]);
        diff.Changes.Select(c => c.Kind).ShouldBe(["connect", "disconnect"]);
        (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={a}&plan={second.Id}", Ct))!.Physical!.Hops.ShouldNotContain(h => h.TerminalId == b);

        var cycle = await client.PutAsJsonAsync($"/api/plans/{first.Id}/dependencies", new { dependsOn = new[] { second.Id } }, Ct);
        cycle.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await cycle.Content.ReadAsStringAsync(Ct)).ShouldContain("cykel");
        (await client.PutAsJsonAsync($"/api/plans/{first.Id}/dependencies", new { dependsOn = new[] { first.Id } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Applying_brings_the_plan_into_production_and_flags_plans_that_no_longer_fit()
    {
        var (db, api) = await NetworkAsync(13);
        await using var _db = db;
        await using var _api = api;
        using var client = NetworkFixture.Client(api);
        var (a, b) = await FreePortsAsync(db);
        var site = await Count(db, "SELECT min(id) FROM site");

        var first = await CreateAsync(client, "Patchning");
        await AddAsync(client, first.Id, new { kind = "connect", a, b, connectionKind = "patch" });
        await AddAsync(client, first.Id, new { kind = "rename", type = "site", objectId = site, name = "Nytt namn" });
        var second = await CreateAsync(client, "Bygger på patchningen", first.Id);
        await AddAsync(client, second.Id, new { kind = "disconnect", a, b });
        // A plan that does the same thing on its own: once the first is in production, its connect no longer fits.
        var rival = await CreateAsync(client, "Samma koppling");
        await PutDependenciesAsync(client, rival.Id, first.Id);
        var rivalOp = await AddAsync(client, rival.Id, new { kind = "connect", a, b, connectionKind = "patch" });
        rivalOp.Problem.ShouldBe("Terminalerna är redan kopplade.");
        await PutDependenciesAsync(client, rival.Id);

        (await client.PostAsync($"/api/plans/{second.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var applied = (await (await client.PostAsync($"/api/plans/{first.Id}/apply", null, Ct)).Content.ReadFromJsonAsync<ApplyResult>(Ct))!;
        applied.Plan.Status.ShouldBe("applied");
        applied.Plan.AppliedBy.ShouldBe("cmdb-demo-full");
        (await Count(db, $"SELECT count(*) FROM connection WHERE valid_to IS NULL AND a_terminal_id = {Math.Min(a, b)} AND b_terminal_id = {Math.Max(a, b)}")).ShouldBe(1);
        (await Count(db, $"SELECT count(*) FROM site WHERE id = {site} AND name = 'Nytt namn'")).ShouldBe(1);
        // The second plan still fits (its disconnect now acts on production); the rival does not.
        applied.Flagged.ShouldBeEmpty("the rival does not depend on the applied plan");

        await ApiFactory.GraphCaughtUpAsync(api.Services, db);
        (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={a}", Ct))!.Physical!.Hops.ShouldContain(h => h.TerminalId == b);
        var rivalView = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{rival.Id}/view", Ct))!;
        rivalView.Problems.ShouldBe(1);
        (await client.PostAsync($"/api/plans/{rival.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/plans/{first.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var secondView = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{second.Id}/view", Ct))!;
        secondView.Plans.Select(p => p.Id).ShouldBe([second.Id]);
        secondView.Problems.ShouldBe(0);
    }

    [Fact]
    public async Task Plans_building_on_one_that_is_applied_or_cancelled_are_flagged()
    {
        var (db, api) = await NetworkAsync(14);
        await using var _db = db;
        await using var _api = api;
        using var client = NetworkFixture.Client(api);
        var ports = await FreePortsAsync(db, 6);

        var first = await CreateAsync(client, "Etapp 1");
        await AddAsync(client, first.Id, new { kind = "connect", a = ports[0], b = ports[1], connectionKind = "patch" });
        var fits = await CreateAsync(client, "Etapp 2", first.Id);
        await AddAsync(client, fits.Id, new { kind = "connect", a = ports[2], b = ports[3], connectionKind = "patch" });
        var clashes = await CreateAsync(client, "Etapp 2b", first.Id);
        await AddAsync(client, clashes.Id, new { kind = "connect", a = ports[4], b = ports[5], connectionKind = "splice" });
        // Production moves under the plans: someone patches 4–5 directly.
        await Exec(db, $"INSERT INTO connection (a_terminal_id, b_terminal_id, kind, lifecycle) VALUES ({ports[4]}, {ports[5]}, 'patch', 'in_service')");
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);

        var applied = (await (await client.PostAsync($"/api/plans/{first.Id}/apply", null, Ct)).Content.ReadFromJsonAsync<ApplyResult>(Ct))!;
        applied.Flagged.Select(p => p.Id).ShouldBe([clashes.Id]);
        applied.Flagged.Single().Flag.ShouldBe("Efter att Etapp 1 fördes in passar 1 operationer inte längre.");

        var later = await CreateAsync(client, "Etapp 3", fits.Id);
        var cancel = (await (await client.PostAsync($"/api/plans/{fits.Id}/cancel", null, Ct)).Content.ReadFromJsonAsync<ApplyResult>(Ct))!;
        cancel.Plan.Status.ShouldBe("cancelled");
        cancel.Flagged.Select(p => p.Id).ShouldBe([later.Id]);
        cancel.Flagged.Single().Flag.ShouldBe("Beroendet Etapp 2 avbröts.");
        (await client.PostAsJsonAsync("/api/plans", new { name = "På en avbruten", dependsOn = new[] { fits.Id } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Editing a flagged plan clears the flag.
        var site = await Count(db, "SELECT min(id) FROM site");
        await AddAsync(client, later.Id, new { kind = "set_lifecycle", type = "site", objectId = site, lifecycle = "decommissioning" });
        (await client.GetFromJsonAsync<PlanDetail>($"/api/plans/{later.Id}", Ct))!.Plan.Flag.ShouldBeNull();
    }

    [Fact]
    public async Task Scopes_limit_what_a_plan_can_touch_and_who_sees_plans()
    {
        var (db, api) = await NetworkAsync(15);
        await using var _db = db;
        await using var _api = api;
        using var full = NetworkFixture.Client(api);
        using var region = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var (a, b) = await FreePortsAsync(db);
        var plan = await CreateAsync(full, "Synlig för hela nätet");

        (await full.GetFromJsonAsync<List<PlanSummary>>("/api/plans", Ct))!.ShouldContain(p => p.Id == plan.Id);
        // Region Nord has no plans in its scope and may not write.
        (await region.GetFromJsonAsync<List<PlanSummary>>("/api/plans", Ct))!.ShouldBeEmpty();
        (await region.GetAsync($"/api/plans/{plan.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await region.GetAsync($"/api/plans/{plan.Id}/view", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await region.GetAsync($"/api/trace?terminal={a}&plan={plan.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await region.PostAsJsonAsync("/api/plans", new { name = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Unknown terminals and objects are refused without saying whether they exist.
        (await full.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "connect", a, b = 999_999_999L, connectionKind = "patch" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await full.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "rename", type = "site", objectId = 999_999_999L, name = "x" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await full.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "connect", a, b = a, connectionKind = "patch" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Count(db, $"SELECT count(*) FROM plan_operation WHERE plan_id = {plan.Id}")).ShouldBe(0);
        _ = b;
    }

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> NetworkAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Embedded), reset: false, TextWriter.Null, ct: Ct);
        // The generator's demo plans claim free ports too; these tests start without plans.
        await using (var clear = db.CreateCommand("DELETE FROM plan_operation; DELETE FROM plan_dependency; DELETE FROM plan"))
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

    private static async Task<PlanSummary> CreateAsync(HttpClient client, string name, params long[] dependsOn)
    {
        var response = await client.PostAsJsonAsync("/api/plans", new { name, dependsOn }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
    }

    private static async Task PutDependenciesAsync(HttpClient client, long plan, params long[] dependsOn)
    {
        var response = await client.PutAsJsonAsync($"/api/plans/{plan}/dependencies", new { dependsOn }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<PlanOperationView> AddAsync(HttpClient client, long plan, object operation)
    {
        var response = await client.PostAsJsonAsync($"/api/plans/{plan}/operations", operation, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanOperationView>(Ct))!;
    }

    /// <summary>Two unconnected ports, a &lt; b.</summary>
    private static async Task<(long A, long B)> FreePortsAsync(NpgsqlDataSource db)
    {
        var ids = await FreePortsAsync(db, 2);
        return (ids[0], ids[1]);
    }

    /// <summary>Unconnected ports in id order.</summary>
    private static async Task<List<long>> FreePortsAsync(NpgsqlDataSource db, int count)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT p.terminal_id FROM port p
            WHERE NOT EXISTS (SELECT 1 FROM connection c WHERE c.a_terminal_id = p.terminal_id OR c.b_terminal_id = p.terminal_id)
            ORDER BY p.terminal_id LIMIT {count}
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> Count(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

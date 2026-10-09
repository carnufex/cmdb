using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Cmdb.Api.Features.Reconciliation;
using Cmdb.Catalog;
using Cmdb.DataGen.Exchange;
using Cmdb.Exchange;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Reconciliation;

/// <summary>
/// Reconciliation (#216, ADR-0020): a source's data compared with cmdb becomes plans, a trusted one brought in at once
/// and one for review, and a report of what differs, is missing or lies outside the caller's scopes.
/// </summary>
public sealed class ReconciliationTests(ApiFactory factory) : IDisposable
{
    private const string Nms = "acme-nms";
    private const string Monitor = "acme-monitor";

    private readonly string _folder = Directory.CreateTempSubdirectory("cmdb-reconcile-test-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Example => Path.Combine(AppContext.BaseDirectory, "exempel", "import");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Owned_changes_become_plans_and_the_trusted_one_is_brought_in_at_once()
    {
        await using var db = await ImportedAsync();
        await using var api = await ApiAsync(db);
        using var client = NetworkFixture.Client(api);
        Copy(Example, _folder);
        Edit(ExchangeFormat.Equipment, "SN-EX-0005", "SN-EX-0055");
        Edit(ExchangeFormat.Sites, "Exempelradio", "Radiosite Exempel");

        var report = await ReconcileAsync(client, Nms);

        report.Errors.ShouldBeEmpty();
        report.AutoApplyProblem.ShouldBeNull();
        // The serial number is acme-nms's to set without review; the site's name is its own but goes through review.
        (await Text(db, "SELECT attributes->>'serialNumber' FROM equipment WHERE external_id = 'ex-e5'")).ShouldBe("SN-EX-0055");
        (await Text(db, $"SELECT status FROM plan WHERE id = {report.AppliedPlanId}")).ShouldBe("applied");
        (await Text(db, "SELECT name FROM site WHERE external_id = 'ex-site-2'")).ShouldBe("Exempelradio");
        (await Text(db, $"SELECT string_agg(kind || ':' || (payload->>'name'), ',') FROM plan_operation WHERE plan_id = {report.ReviewPlanId}"))
            .ShouldBe("rename:Radiosite Exempel");
        var sites = report.Counts.Single(c => c.ObjectType == "site");
        (sites.Matched, sites.Changed, sites.New, sites.Missing).ShouldBe((2, 1, 0, 0));
        report.NotReconciled.ShouldContain(n => n.StartsWith(ExchangeFormat.Locations, StringComparison.Ordinal));

        // A new run supersedes the previous plan for review instead of proposing the same thing twice.
        var again = await ReconcileAsync(client, Nms);
        (await Text(db, $"SELECT status FROM plan WHERE id = {report.ReviewPlanId}")).ShouldBe("cancelled");
        (await Scalar(db, $"SELECT count(*) FROM plan_operation WHERE plan_id = {again.ReviewPlanId}")).ShouldBe(1);
        again.AppliedPlanId.ShouldBeNull();
        (await Text(db, "SELECT reported->>'attributes.serialNumber' FROM source_record r JOIN equipment e ON e.id = r.object_id AND r.object_type = 'equipment' WHERE e.external_id = 'ex-e5'"))
            .ShouldBe("SN-EX-0055");

        var listed = await client.GetFromJsonAsync<List<ReconciliationSummary>>("/api/reconciliations", Ct);
        listed!.Select(r => r.Id).ShouldContain(again.Id);
        (await client.GetFromJsonAsync<ReconciliationReport>($"/api/reconciliations/{report.Id}", Ct))!.ReviewPlanId.ShouldBe(report.ReviewPlanId);
    }

    [Fact]
    public async Task A_second_source_is_linked_by_the_matching_rules_and_what_it_does_not_own_is_a_deviation()
    {
        await using var db = await ImportedAsync();
        await using var api = await ApiAsync(db);
        using var client = NetworkFixture.Client(api);
        // The monitoring system knows the same sites and equipment under its own ids.
        Copy(Example, _folder, ExchangeFormat.Sites, ExchangeFormat.Locations, ExchangeFormat.Equipment);
        foreach (var file in new[] { ExchangeFormat.Sites, ExchangeFormat.Locations, ExchangeFormat.Equipment })
        {
            File.WriteAllText(Path.Combine(_folder, file), File.ReadAllText(Path.Combine(_folder, file)).Replace("ex-", "mon-", StringComparison.Ordinal));
        }
        Edit(ExchangeFormat.Equipment, "acme-ax-24,in_service", "acme-ax-24,decommissioning");
        Edit(ExchangeFormat.Equipment, "EX-NAV-1 CR-8 1", "nav1-cr8");

        var report = await ReconcileAsync(client, Monitor);

        report.Errors.ShouldBeEmpty();
        var equipment = report.Counts.Single(c => c.ObjectType == "equipment");
        // Linked on serial number: the router, its card and the switch. The two ODFs have none.
        (equipment.Matched, equipment.Linked).ShouldBe((3, 3));
        report.Counts.Single(c => c.ObjectType == "site").Linked.ShouldBe(2);
        (await Scalar(db, $"SELECT count(*) FROM source_record WHERE source_system = '{Monitor}'")).ShouldBe(5);
        // acme-nms ranks above it for the lifecycle; nobody does for the name.
        report.Deviations.ShouldContain(d => d.Attribute == "lifecycle" && d.Reason == "owned-by-other" && d.ExternalId == "mon-e5");
        (await Text(db, $"SELECT string_agg(kind, ',' ORDER BY kind) FROM plan_operation WHERE plan_id = {report.ReviewPlanId}"))
            .ShouldBe("create_equipment,create_equipment,rename");
        // The ODFs without serial numbers are proposed as new, in the racks the source names.
        equipment.New.ShouldBe(2);
        (await Text(db, $"SELECT string_agg(payload->>'rack', ',' ORDER BY payload->>'rack') FROM plan_operation WHERE plan_id = {report.ReviewPlanId} AND kind = 'create_equipment'"))
            .ShouldBe("Rack 1,Skåp");
    }

    [Fact]
    public async Task What_the_source_no_longer_reports_is_marked_and_a_dry_run_writes_nothing()
    {
        await using var db = await ImportedAsync();
        await using var api = await ApiAsync(db);
        using var client = NetworkFixture.Client(api);
        Copy(Example, _folder);
        var path = Path.Combine(_folder, ExchangeFormat.Equipment);
        File.WriteAllLines(path, File.ReadAllLines(path).Where(l => !l.StartsWith("ex-e5,", StringComparison.Ordinal)));
        Edit(ExchangeFormat.Sites, "Exempelradio", "Radiosite Exempel");

        var dry = await ReconcileAsync(client, Nms, dryRun: true);
        dry.Counts.Single(c => c.ObjectType == "equipment").Missing.ShouldBe(1);
        dry.Operations.ShouldBe(1);
        (dry.ReviewPlanId, dry.AppliedPlanId).ShouldBe((null, null));
        (await Scalar(db, "SELECT count(*) FROM source_record WHERE missing_since IS NOT NULL")).ShouldBe(0);
        (await Scalar(db, "SELECT count(*) FROM plan")).ShouldBe(0);

        var run = await ReconcileAsync(client, Nms);
        run.Deviations.ShouldContain(d => d.ExternalId == "ex-e5" && d.Reason == "missing");
        (await Scalar(db, "SELECT count(*) FROM source_record r JOIN equipment e ON e.id = r.object_id AND r.object_type = 'equipment' WHERE e.external_id = 'ex-e5' AND r.missing_since IS NOT NULL"))
            .ShouldBe(1);
        (await Text(db, "SELECT lifecycle::text FROM equipment WHERE external_id = 'ex-e5'")).ShouldBe("in_service");
    }

    [Fact]
    public async Task Objects_outside_the_callers_scopes_are_counted_and_never_named_or_changed()
    {
        await using var db = await ImportedAsync();
        await using var api = await ApiAsync(db);
        await Exec(db, """
            INSERT INTO access_scope (key, name, area, site_types, hidden_attributes, plans, crossing_mode, groups, db_roles, reason, granted_by, approved_by)
            VALUES ('test-nav', 'Test nav', NULL, '{hub}', '{}', '{*}', 'whole', '{cmdb-test-nav}', '{}', 'test', 'a', 'b')
            """);
        await Cmdb.Database.Scopes.ScopeVisibility.RefreshAsync(db, Ct);
        var registry = api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRegistry>();
        await registry.LoadAsync(Ct);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new(Cmdb.Api.Auth.CmdbClaims.Groups, "cmdb-test-nav"), new(ClaimTypes.Name, "integration")], "test"));
        Copy(Example, _folder);
        Edit(ExchangeFormat.Equipment, "SN-EX-0005", "SN-EX-0055");
        Edit(ExchangeFormat.Equipment, "SN-EX-0001", "SN-EX-0011");

        using var services = api.Services.CreateScope();
        var report = await services.ServiceProvider.GetRequiredService<Reconciler>()
            .RunAsync(user, registry.For(user), Nms, _folder, dryRun: false, Ct);

        // The radio site and its equipment are outside: counted, not named, not changed.
        report.Counts.Single(c => c.ObjectType == "site").OutsideScope.ShouldBe(1);
        report.Counts.Single(c => c.ObjectType == "equipment").OutsideScope.ShouldBe(2);
        report.Deviations.ShouldNotContain(d => d.ExternalId == "ex-site-2" || d.ExternalId == "ex-e4" || d.ExternalId == "ex-e5");
        (await Text(db, "SELECT attributes->>'serialNumber' FROM equipment WHERE external_id = 'ex-e5'")).ShouldBe("SN-EX-0005");
        (await Text(db, "SELECT attributes->>'serialNumber' FROM equipment WHERE external_id = 'ex-e1'")).ShouldBe("SN-EX-0011");
    }

    [Fact]
    public async Task An_archive_with_other_files_is_refused()
    {
        await using var db = await factory.NewDatabaseAsync();
        await using var api = await ApiAsync(db);
        using var client = NetworkFixture.Client(api);
        File.WriteAllText(Path.Combine(_folder, "secrets.txt"), "x");

        var response = await client.PostAsync("/api/reconciliations", Form(Nms, false), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("utbytesformatets filer");
    }

    private async Task<NpgsqlDataSource> ImportedAsync()
    {
        var db = await factory.NewDatabaseAsync();
        (await NetworkImport.RunAsync(db, Example, Nms, TypeCatalog.Current, dryRun: false, TextWriter.Null, Ct)).Errors.ShouldBeEmpty();
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

    private async Task<ReconciliationReport> ReconcileAsync(HttpClient client, string source, bool dryRun = false)
    {
        var response = await client.PostAsync("/api/reconciliations", Form(source, dryRun), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ReconciliationReport>(Ct))!;
    }

    private MultipartFormDataContent Form(string source, bool dryRun)
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(_folder))
            {
                archive.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }
        zip.Position = 0;
        return new MultipartFormDataContent
        {
            { new StringContent(source), "source" },
            { new StringContent(dryRun ? "true" : "false"), "dryRun" },
            { new StreamContent(zip), "file", "export.zip" },
        };
    }

    private void Edit(string file, string from, string to)
    {
        var path = Path.Combine(_folder, file);
        var text = File.ReadAllText(path);
        text.ShouldContain(from);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    private static void Copy(string from, string to, params string[] only)
    {
        foreach (var file in Directory.GetFiles(from, "*.csv").Where(f => only.Length == 0 || only.Contains(Path.GetFileName(f))))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
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

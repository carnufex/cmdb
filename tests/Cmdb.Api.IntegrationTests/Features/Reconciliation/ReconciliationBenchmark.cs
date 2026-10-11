using System.Diagnostics;
using System.Security.Claims;
using Cmdb.Api.Features.Reconciliation;
using Cmdb.Exchange;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Reconciliation;

/// <summary>
/// Reconciliation in full scale (#230), against a database the exchange folder was imported into. Skipped unless
/// <c>CMDB_RECONCILE_BENCH</c> is <c>&lt;connection string&gt;|&lt;folder&gt;</c>; see docs/avstamning.md for how the numbers there were made.
/// </summary>
public sealed class ReconciliationBenchmark(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Full_scale()
    {
        var setting = Environment.GetEnvironmentVariable("CMDB_RECONCILE_BENCH");
        Assert.SkipWhen(string.IsNullOrEmpty(setting), "Set CMDB_RECONCILE_BENCH=<connection string>|<folder> to measure reconciliation in full scale.");
        var (connectionString, folder) = (setting!.Split('|')[0], setting.Split('|')[1]);
        await using (var db = Cmdb.Database.CmdbDatabase.CreateDataSource(connectionString))
        await using (var context = Cmdb.Database.CmdbDatabase.CreateContext(db))
        {
            await Cmdb.Database.Scopes.ScopeCatalog.SyncAsync(context, Ct);
        }
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromMinutes(10), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromMinutes(10), Ct);
        var registry = api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRegistry>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new(Cmdb.Api.Auth.CmdbClaims.Groups, "cmdb-full"), new(ClaimTypes.Name, "benchmark")], "test"));

        async Task<ReconciliationReport> Run(string from, bool dryRun, string what)
        {
            using var services = api.Services.CreateScope();
            var sw = Stopwatch.StartNew();
            var report = await services.ServiceProvider.GetRequiredService<Reconciler>().RunAsync(user, registry.For(user), "acme-nms", from, dryRun, Ct);
            TestContext.Current.SendDiagnosticMessage($"{what}: {sw.Elapsed.TotalSeconds:F1} s, {report.Operations} operations, "
                + string.Join(", ", report.Reasons.Select(r => $"{r.Key} {r.Value}")) + "; "
                + string.Join(", ", report.Counts.Select(c => $"{c.ObjectType} {c.Reported}/{c.Matched}/{c.New}/{c.Missing}")));
            report.Errors.ShouldBeEmpty();
            return report;
        }

        (await Run(folder, dryRun: true, "dry run, unchanged")).Operations.ShouldBe(0);
        (await Run(folder, dryRun: false, "unchanged")).Operations.ShouldBe(0);

        // Every 3 000th connection gone and every 1 000th circuit's path reversed.
        var changed = Directory.CreateTempSubdirectory("cmdb-reconcile-bench-").FullName;
        try
        {
            foreach (var file in Directory.GetFiles(folder, "*.csv"))
            {
                File.Copy(file, Path.Combine(changed, Path.GetFileName(file)));
            }
            var connections = Path.Combine(changed, ExchangeFormat.Connections);
            File.WriteAllLines(connections, File.ReadAllLines(connections).Where((_, i) => i == 0 || i % 3000 != 0));
            var hops = File.ReadAllLines(Path.Combine(changed, ExchangeFormat.Hops));
            var reversed = hops.Skip(1).Select(l => l.Split(',')).GroupBy(f => f[0]).Select((g, i) => (g, i))
                .SelectMany(x => x.i % 1000 == 0
                    ? x.g.Reverse().Select((f, seq) => string.Join(',', [f[0], seq.ToString(System.Globalization.CultureInfo.InvariantCulture), .. f[2..]]))
                    : x.g.Select(f => string.Join(',', f)));
            File.WriteAllLines(Path.Combine(changed, ExchangeFormat.Hops), [hops[0], .. reversed]);
            var report = await Run(changed, dryRun: false, "connections gone, paths reversed");
            report.Counts.Single(c => c.ObjectType == "connection").Missing.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(changed, recursive: true);
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cmdb.Api.Features.Operations;
using Cmdb.Catalog;
using Cmdb.Database;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Operations;

/// <summary>
/// The operation log through the API (#23): a write says who, through which client and why; the object's history shows
/// the change and its operation; the hash chain can be checked; the map can be drawn as it was or as it was known; every
/// reading call is logged with a hash of its answer; and the partitions are made ahead and the read log pruned.
/// </summary>
public sealed class OperationApiTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_change_through_the_api_is_an_operation_in_the_objects_history_and_the_hash_chain()
    {
        var (db, api) = await ApiAsync(54);
        await using var _ = api;
        using var client = NetworkFixture.Client(api);
        await WaitAsync(db, "SELECT hash_chain FROM operation_config");
        var site = await Scalar<long>(db, "SELECT min(id) FROM site WHERE lifecycle = 'in_service'");
        var oldName = await Scalar<string>(db, $"SELECT name FROM site WHERE id = {site}");

        (await client.PatchAsJsonAsync($"/api/sites/{site}", new { name = "Omdöpt i test" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var history = (await client.GetFromJsonAsync<ObjectHistory>($"/api/history/site/{site}", Ct))!;
        history.Entries.Select(e => e.Kind).ShouldBe(["changed", "created"]);
        var change = history.Entries[0];
        change.Operation.ShouldNotBeNull();
        change.Operation.Actor.ShouldBe("cmdb-demo-full");
        change.Operation.Client.ShouldBe(ApiFactory.Audience);
        change.Operation.Command.ShouldBe("update_object");
        change.Operation.Chained.ShouldBeTrue();
        change.Changes.ShouldBe([new FieldChange("name", oldName, "Omdöpt i test")]);

        var log = (await client.GetFromJsonAsync<OperationList>("/api/operations?limit=5", Ct))!;
        log.Items[0].Id.ShouldBe(change.Operation.Id);
        log.Items[0].Payload.GetProperty("id").GetInt64().ShouldBe(site);

        var verified = (await client.GetFromJsonAsync<ChainVerification>("/api/operations/verify", Ct))!;
        verified.Enabled.ShouldBeTrue();
        verified.Intact.ShouldBeTrue();
        verified.Chained.ShouldBeGreaterThan(0);
        // A row changed afterwards breaks the chain there.
        await Exec(db, $"UPDATE operation SET why = 'ändrat i efterhand' WHERE id = {change.Operation.Id}");
        verified = (await client.GetFromJsonAsync<ChainVerification>("/api/operations/verify", Ct))!;
        verified.Intact.ShouldBeFalse();
        verified.Breaks.ShouldContain(b => b.Id == change.Operation.Id);

        // The log names objects anywhere, so it is for those who see everything; history follows the object's scope.
        using var regional = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await regional.GetAsync("/api/operations", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await regional.GetAsync("/api/operations/verify", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var inside = await Scalar<long>(db, "SELECT min(site_id) FROM scope_site WHERE scope_key = 'region-nord'");
        var outside = await Scalar<long>(db, "SELECT min(s.id) FROM site s WHERE NOT EXISTS (SELECT 1 FROM scope_site z WHERE z.scope_key = 'region-nord' AND z.site_id = s.id)");
        (await regional.GetAsync($"/api/history/site/{inside}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await regional.GetAsync($"/api/history/site/{outside}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/history/port/1", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_map_is_drawn_as_it_was_or_as_it_was_known_at_a_time()
    {
        var (db, api) = await ApiAsync(55);
        await using var _ = api;
        using var client = NetworkFixture.Client(api);
        // A hub shows at every zoom, so the national tile has it.
        var hub = await Scalar<long>(db, $"SELECT min(id) FROM site WHERE lifecycle = 'in_service' AND site_type = ANY('{{{string.Join(',', TypeCatalog.Current.SiteTypesWith(CatalogRoles.Hub))}}}')");
        var oldName = await Scalar<string>(db, $"SELECT name FROM site WHERE id = {hub}");
        var before = await Scalar<DateTime>(db, "SELECT now()");
        await Task.Delay(20, Ct);
        await Exec(db, $"UPDATE site SET name = 'Tidsresa nytt namn' WHERE id = {hub}");
        var at = Uri.EscapeDataString(new DateTimeOffset(before, TimeSpan.Zero).ToString("o"));

        var now = await client.GetByteArrayAsync("/api/tiles/0/0/0", Ct);
        Contains(now, "Tidsresa nytt namn").ShouldBeTrue();
        Contains(now, oldName).ShouldBeFalse();
        // What the CMDB said then: the version recorded at the time.
        var known = await client.GetByteArrayAsync($"/api/tiles/0/0/0?at={at}&mode=knowledge", Ct);
        Contains(known, oldName).ShouldBeTrue();
        Contains(known, "Tidsresa nytt namn").ShouldBeFalse();
        // What was in service then, as we know it now: today's name.
        var real = await client.GetByteArrayAsync($"/api/tiles/0/0/0?at={at}&mode=reality", Ct);
        Contains(real, "Tidsresa nytt namn").ShouldBeTrue();

        // Before the network was built there is nothing; it grew over the years; nothing was known before it was loaded.
        (await client.GetByteArrayAsync("/api/tiles/0/0/0?at=1985-01-01T00:00:00Z", Ct)).ShouldBeEmpty();
        var early = await client.GetByteArrayAsync("/api/tiles/0/0/0?at=2001-01-01T00:00:00Z", Ct);
        early.Length.ShouldBeGreaterThan(0);
        early.Length.ShouldBeLessThan(real.Length);
        (await client.GetByteArrayAsync("/api/tiles/0/0/0?at=2010-01-01T00:00:00Z&mode=knowledge", Ct)).ShouldBeEmpty();
        (await client.GetAsync("/api/tiles/0/0/0?at=2010-01-01T00:00:00Z&mode=other", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_reading_call_is_logged_with_its_scope_and_a_hash_of_the_answer()
    {
        var (db, api) = await ApiAsync(56);
        await using var _ = api;
        using var client = NetworkFixture.Client(api);
        var cable = await Scalar<long>(db, "SELECT min(cable_id) FROM cable_path");
        var response = await client.GetAsync($"/api/cables/{cable}/path", Ct);
        var body = await response.Content.ReadAsByteArrayAsync(Ct);
        var steps = JsonDocument.Parse(body).RootElement.GetArrayLength();
        steps.ShouldBeGreaterThan(0);

        var row = await LoggedAsync(db, $"/api/cables/{cable}/path");
        row.ShouldBe(["cmdb-demo-full", ApiFactory.Audience, "api", "*", Convert.ToHexStringLower(SHA256.HashData(body)), steps, (short)200]);

        // A regional reader is logged with its scopes, also when the answer is that there is nothing.
        using var regional = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await regional.GetAsync("/api/history/site/999999999", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var denied = await LoggedAsync(db, "/api/history/site/999999999");
        denied[3].ShouldBe("region-nord");
        denied[6].ShouldBe((short)404);

        // An agent's tool call is logged with the tool and its arguments.
        var http = NetworkFixture.Client(api);
        await using (var mcp = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: true),
            cancellationToken: Ct))
        {
            await mcp.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "läsloggtest" }, cancellationToken: Ct);
        }
        var tool = await LoggedAsync(db, "/mcp tools/call search%läsloggtest%");
        tool[2].ShouldBe("mcp");
        tool[4].ShouldNotBeNull();

        // Writes are operations, not reads.
        (await Scalar<long>(db, "SELECT count(*) FROM read_log WHERE query LIKE 'PATCH%'")).ShouldBe(0);
    }

    [Fact]
    public async Task Partitions_are_made_ahead_and_the_read_log_is_archived_and_dropped_after_its_retention()
    {
        var archive = Path.Combine(Path.GetTempPath(), "cmdb-readlog-" + Guid.NewGuid().ToString("N"));
        var (db, api) = await ApiAsync(57, ("ReadLog:ArchivePath", archive), ("ReadLog:RetentionDays", "90"));
        await using var _ = api;
        var now = DateTimeOffset.UtcNow;

        // A row written before its month had a partition is moved into it when the partition is made.
        var future = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(7).AddDays(3);
        await Exec(db, $"INSERT INTO operation (at, actor, command) VALUES ('{future:o}', 'test', 'framtid')");
        (await Scalar<string>(db, "SELECT tableoid::regclass::text FROM operation WHERE command = 'framtid'")).ShouldBe("operation_default");
        await OperationLog.EnsurePartitionsAsync(db, future, monthsAhead: 0, daysAhead: 0, Ct);
        (await Scalar<string>(db, "SELECT tableoid::regclass::text FROM operation WHERE command = 'framtid'")).ShouldBe($"operation_{future:'y'yyyy'm'MM}");

        // An old day of the read log, as if the service had run back then.
        var old = now.AddDays(-120);
        await OperationLog.EnsurePartitionsAsync(db, old, monthsAhead: 0, daysAhead: 0, Ct);
        await Exec(db, $"INSERT INTO read_log (at, actor, surface, query, scope, status, elapsed_ms) VALUES ('{old:o}', 'test', 'api', '/api/gammalt', '*', 200, 1)");
        await Exec(db, $"INSERT INTO read_log (at, actor, surface, query, scope, status, elapsed_ms) VALUES ('{now.AddDays(-200):o}', 'test', 'api', '/api/default', '*', 200, 1)");

        await api.Services.GetRequiredService<OperationLogMaintenance>().RunAsync(now, Ct);

        var partition = OperationLog.ReadLogPartition(new DateTimeOffset(old.UtcDateTime.Date, TimeSpan.Zero));
        (await Scalar<bool>(db, $"SELECT to_regclass('{partition}') IS NULL")).ShouldBeTrue();
        (await Scalar<long>(db, "SELECT count(*) FROM read_log WHERE query IN ('/api/gammalt', '/api/default')")).ShouldBe(0);
        var file = Path.Combine(archive, partition + ".csv.gz");
        File.Exists(file).ShouldBeTrue();
        await using (var gzip = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
        {
            (await reader.ReadToEndAsync(Ct)).ShouldContain("/api/gammalt");
        }
        Directory.Delete(archive, recursive: true);

        // This month and the next two, today and three days ahead.
        foreach (var table in new[] { "operation" }.Concat(OperationLog.HistoryTables.Select(t => t + "_history")))
        {
            for (var i = 0; i <= 2; i++)
            {
                var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(i);
                (await Scalar<bool>(db, $"SELECT to_regclass('{table}_{month:'y'yyyy'm'MM}') IS NOT NULL")).ShouldBeTrue(table);
            }
        }
        (await Scalar<bool>(db, $"SELECT to_regclass('{OperationLog.ReadLogPartition(now.AddDays(3))}') IS NOT NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task Every_history_table_has_the_columns_of_its_table()
    {
        // A migration that adds a column to a table with history adds it to the history table too (ADR-0013).
        foreach (var table in OperationLog.HistoryTables)
        {
            var columns = await Scalar<string>(factory.Db, $"""
                SELECT string_agg(column_name || ':' || data_type, ',' ORDER BY ordinal_position) FROM information_schema.columns
                WHERE table_name = '{table}'
                """);
            var history = await Scalar<string>(factory.Db, $"""
                SELECT string_agg(column_name || ':' || data_type, ',' ORDER BY ordinal_position) FROM information_schema.columns
                WHERE table_name = '{table}_history' AND column_name NOT IN ('sys_to', 'operation_id')
                """);
            history.ShouldBe(columns, table);
        }
    }

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> ApiAsync(int seed, params (string Key, string Value)[] settings)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Cmdb", connectionString);
            b.UseSetting("Operations:HashChain", "true");
            b.UseSetting("ReadLog:FlushMilliseconds", "50");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return (db, api);
    }

    /// <summary>The read log's entry for a query (a LIKE pattern), once the writer has written it.</summary>
    private static async Task<object?[]> LoggedAsync(NpgsqlDataSource db, string query)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            await using var cmd = db.CreateCommand("""
                SELECT actor, client, surface, scope, encode(result_hash, 'hex'), rows, status FROM read_log
                WHERE query LIKE $1 ORDER BY at DESC LIMIT 1
                """);
            cmd.Parameters.Add(new() { Value = query });
            await using var reader = await cmd.ExecuteReaderAsync(Ct);
            if (await reader.ReadAsync(Ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                return row;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No read log entry for {query}.");
            }
            await Task.Delay(50, Ct);
        }
    }

    private static async Task WaitAsync(NpgsqlDataSource db, string condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await Scalar<bool>(db, condition))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(condition);
            }
            await Task.Delay(50, Ct);
        }
    }

    private static bool Contains(byte[] tile, string text) => tile.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private static async Task<T> Scalar<T>(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}

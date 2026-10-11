using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Grid;
using Cmdb.Api.Features.Objects;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Selection;

/// <summary>
/// A selection's actions (#253): the impact if everything in it fails, the CSV export inside the caller's scopes, and
/// the lasso picking cables as well as sites.
/// </summary>
public sealed class SelectionTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_selection_fails_together_exports_what_the_scope_shows_and_the_lasso_picks_its_cables()
    {
        var db = await factory.NewDatabaseAsync();
        await using var dbScope = db;
        await Loader.LoadAsync(db, NetworkBuilder.Build(37, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        await Exec(db, """
            INSERT INTO access_scope (key, name, area, site_types, hidden_attributes, plans, crossing_mode, groups, db_roles, reason, granted_by, approved_by)
            VALUES ('test-dolt', 'Test dolt', NULL, '{}', '{backupHours,coordinates}', '{*}', 'whole', '{cmdb-test-dolt}', '{}', 'test', 'a', 'b')
            """);
        await Cmdb.Database.Scopes.ScopeVisibility.RefreshAsync(db, Ct);
        await using var api = await ApiAsync(db);
        using var client = NetworkFixture.Client(api);

        // The two sites with the most circuits through their ports, and a cable carrying circuits.
        var sites = await Ids(db, """
            SELECT e.site_id FROM equipment e JOIN port p ON p.equipment_id = e.id JOIN circuit_hop h ON h.terminal_id = p.terminal_id
            GROUP BY e.site_id ORDER BY count(DISTINCT h.circuit_id) DESC, e.site_id LIMIT 2
            """);
        var cable = (await Ids(db, """
            SELECT k.cable_id FROM conductor k JOIN conductor_end ce ON ce.conductor_id = k.id JOIN circuit_hop h ON h.terminal_id = ce.terminal_id
            GROUP BY k.cable_id ORDER BY count(*) DESC, k.cable_id LIMIT 1
            """))[0];
        async Task<Impact> ImpactAsync(long[] siteIds, long[] cableIds)
        {
            var response = await client.PostAsJsonAsync("/api/selection/impact", new { siteIds, cableIds }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<Impact>(Ct))!;
        }
        var one = await ImpactAsync([sites[0]], []);
        (await ImpactAsync([sites[0]], [])).Circuits.ShouldBe((await client.GetFromJsonAsync<Impact>($"/api/sites/{sites[0]}/impact", Ct))!.Circuits);
        var all = await ImpactAsync([.. sites], [cable]);
        all.Circuits.ShouldBeGreaterThanOrEqualTo(one.Circuits);
        all.Circuits.ShouldBeGreaterThanOrEqualTo((await client.GetFromJsonAsync<Impact>($"/api/cables/{cable}/impact", Ct))!.Circuits);
        (await client.PostAsJsonAsync("/api/selection/impact", new { siteIds = Array.Empty<long>(), cableIds = Array.Empty<long>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The export: a row per object, and a site's attributes as JSON.
        var withBackup = (await Ids(db, """
            UPDATE site SET attributes = attributes || '{"backupHours": 8}' WHERE id = (SELECT min(id) FROM site WHERE site_type = 'hub') RETURNING id
            """))[0];
        var export = await client.PostAsJsonAsync("/api/selection/export", new { siteIds = new[] { withBackup, sites[0] }, cableIds = new[] { cable } }, Ct);
        export.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var lines = (await export.Content.ReadAsStringAsync(Ct)).TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("Typ;Kod;Namn");
        lines.Length.ShouldBe(withBackup == sites[0] ? 3 : 4);
        lines.ShouldContain(l => l.StartsWith("Kabel;", StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains("backupHours", StringComparison.Ordinal));

        // Someone whose scope hides an attribute and positions gets neither.
        using var hidden = NetworkFixture.Client(api, "cmdb-test", ["cmdb-test-dolt"]);
        var masked = await (await hidden.PostAsJsonAsync("/api/selection/export", new { siteIds = new[] { withBackup }, cableIds = Array.Empty<long>() }, Ct))
            .Content.ReadAsStringAsync(Ct);
        masked.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2, masked);
        masked.ShouldNotContain("backupHours");
        var row = masked.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r').Split(';');
        row.Length.ShouldBeGreaterThanOrEqualTo(7, masked);
        (row[5], row[6]).ShouldBe(("", ""), masked);

        // The lasso around a cable's whole route picks the cable.
        var ring = await Ring(db, cable);
        var lasso = await client.PostAsJsonAsync("/api/sites/within", new { polygon = ring }, Ct);
        lasso.StatusCode.ShouldBe(HttpStatusCode.OK, await lasso.Content.ReadAsStringAsync(Ct));
        var within = (await lasso.Content.ReadFromJsonAsync<WithinResult>(Ct))!;
        within.Cables!.ShouldContain(cable);
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

    /// <summary>A rectangle around the cable's route, a little wider than it.</summary>
    private static async Task<double[][]> Ring(NpgsqlDataSource db, long cable)
    {
        await using var cmd = db.CreateCommand("SELECT ST_XMin(g) - 50, ST_YMin(g) - 50, ST_XMax(g) + 50, ST_YMax(g) + 50 FROM (SELECT geom AS g FROM cable WHERE id = $1) c");
        cmd.Parameters.Add(new() { Value = cable });
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        var (x0, y0, x1, y1) = (reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
        return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]];
    }

    private static async Task<long[]> Ids(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return [.. ids];
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}

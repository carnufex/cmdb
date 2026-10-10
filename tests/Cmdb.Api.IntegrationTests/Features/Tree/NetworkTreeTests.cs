using System.Net.Http.Json;
using Cmdb.Api.Features.Tree;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Tree;

/// <summary>
/// The content tree's root (#250): the whole network by site type, large types split into ranges of at most 500 sites,
/// and only what the caller's scopes show, in the counts too.
/// </summary>
public sealed class NetworkTreeTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_network_is_grouped_by_site_type_and_large_types_are_split_into_ranges_inside_the_scope()
    {
        var db = await factory.NewDatabaseAsync();
        await using var dbScope = db;
        await Loader.LoadAsync(db, NetworkBuilder.Build(36, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        // Enough cabinets in the north for ranges, and one removed site that never shows.
        await Exec(db, """
            INSERT INTO site (code, name, site_type, geom, lifecycle)
            SELECT 'SKP-T' || lpad(n::text, 4, '0'), 'Testskåp ' || n, 'cabinet', ST_SetSRID(ST_MakePoint(700000 + n, 7300000), 3006), 'in_service'
            FROM generate_series(1, 1100) n;
            INSERT INTO site (code, name, site_type, geom, lifecycle)
            VALUES ('SKP-BORT', 'Borttagen', 'cabinet', ST_SetSRID(ST_MakePoint(700000, 7300001), 3006), 'removed');
            """);
        await Cmdb.Database.Scopes.ScopeVisibility.RefreshAsync(db, Ct);
        await using var api = await ApiAsync(db);
        using var client = NetworkFixture.Client(api);

        var root = (await client.GetFromJsonAsync<TreeRoot>("/api/tree", Ct))!;
        root.Label.ShouldBe("Sverige");
        root.Sites.ShouldBe((int)await Scalar(db, "SELECT count(*) FROM site WHERE lifecycle <> 'removed'"));
        root.Groups.Sum(g => g.Sites).ShouldBe(root.Sites);
        // The catalog's order: the backbone first.
        root.Groups[0].SiteType.ShouldBe("hub");
        root.Groups.Single(g => g.SiteType == "radio").Icon.ShouldBe("tower");

        var cabinets = root.Groups.Single(g => g.SiteType == "cabinet");
        var content = (await client.GetFromJsonAsync<TreeGroupContent>("/api/tree/cabinet", Ct))!;
        content.Sites.ShouldBeNull();
        content.Chunks!.ShouldAllBe(c => c.Sites <= 500);
        content.Chunks!.Sum(c => c.Sites).ShouldBe(cabinets.Sites);
        content.Chunks!.Zip(content.Chunks!.Skip(1)).ShouldAllBe(p => string.CompareOrdinal(p.First.To, p.Second.From) < 0);
        var first = content.Chunks![0];
        var sites = (await client.GetFromJsonAsync<TreeGroupContent>(
            $"/api/tree/cabinet?from={Uri.EscapeDataString(first.From)}&to={Uri.EscapeDataString(first.To)}", Ct))!.Sites!;
        sites.Count.ShouldBe(first.Sites);
        sites.ShouldNotContain(s => s.Code == "SKP-BORT");

        // A type with few sites lists them directly.
        var hubs = (await client.GetFromJsonAsync<TreeGroupContent>("/api/tree/hub", Ct))!;
        hubs.Chunks.ShouldBeNull();
        hubs.Sites!.Count.ShouldBe(root.Groups[0].Sites);

        // Someone who sees the north only: the scope's name, and only its sites, in the counts too.
        using var north = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var scoped = (await north.GetFromJsonAsync<TreeRoot>("/api/tree", Ct))!;
        scoped.Label.ShouldBe("Region Nord");
        scoped.Sites.ShouldBe((int)await Scalar(db, """
            SELECT count(*) FROM site s WHERE s.lifecycle <> 'removed'
              AND EXISTS (SELECT 1 FROM scope_site z WHERE z.scope_key = 'region-nord' AND z.site_id = s.id)
            """));
        scoped.Sites.ShouldBeLessThan(root.Sites);
        var visible = (await north.GetFromJsonAsync<TreeGroupContent>("/api/tree/hub", Ct))!.Sites ?? [];
        foreach (var site in visible)
        {
            (await Scalar(db, $"SELECT count(*) FROM scope_site WHERE scope_key = 'region-nord' AND site_id = {site.Id}")).ShouldBe(1);
        }
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
}

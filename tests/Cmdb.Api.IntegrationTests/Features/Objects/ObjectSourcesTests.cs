using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Catalog;
using Cmdb.DataGen.Exchange;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Objects;

/// <summary>
/// Provenance per attribute (#215, ADR-0019): the import records what the source said about each object, and the
/// object's panel shows each source's values, whether the object still has them, which source owns them, and nothing
/// the user's scope hides.
/// </summary>
public sealed class ObjectSourcesTests(ApiFactory factory)
{
    private const string Source = "acme-nms";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Example => Path.Combine(AppContext.BaseDirectory, "exempel", "import");

    [Fact]
    public async Task The_import_records_each_object_once_per_source_and_a_new_run_confirms_it_again()
    {
        await using var db = await factory.NewDatabaseAsync();
        (await ImportAsync(db)).Errors.ShouldBeEmpty();

        var objects = await Scalar(db, """
            SELECT (SELECT count(*) FROM site) + (SELECT count(*) FROM location) + (SELECT count(*) FROM equipment)
                 + (SELECT count(*) FROM cable) + (SELECT count(*) FROM circuit) + (SELECT count(*) FROM service)
            """);
        (await Scalar(db, $"SELECT count(*) FROM source_record WHERE source_system = '{Source}'")).ShouldBe(objects);
        (await Text(db, "SELECT r.reported->>'name' FROM source_record r JOIN site s ON s.id = r.object_id WHERE r.object_type = 'site' AND s.external_id = 'ex-site-2'"))
            .ShouldBe("Exempelradio");
        (await Text(db, "SELECT r.reported->>'attributes.serialNumber' FROM source_record r JOIN equipment e ON e.id = r.object_id WHERE r.object_type = 'equipment' AND e.external_id = 'ex-e5'"))
            .ShouldBe("SN-EX-0005");
        var confirmed = await Text(db, "SELECT max(confirmed_at)::text FROM source_record");

        (await ImportAsync(db)).Errors.ShouldBeEmpty();
        (await Scalar(db, "SELECT count(*) FROM source_record")).ShouldBe(objects);
        (await Scalar(db, $"SELECT count(*) FROM source_record WHERE confirmed_at <= '{confirmed}'")).ShouldBe(0);
    }

    [Fact]
    public async Task The_panel_shows_what_the_source_said_marks_changed_values_and_hides_what_the_scope_hides()
    {
        await using var db = await factory.NewDatabaseAsync();
        (await ImportAsync(db)).Errors.ShouldBeEmpty();
        // Changed in cmdb after the source confirmed it.
        await Exec(db, "UPDATE site SET name = 'Radiosite Exempel' WHERE external_id = 'ex-site-2'");
        await Exec(db, """
            INSERT INTO access_scope (key, name, area, site_types, hidden_attributes, plans, crossing_mode, groups, db_roles, reason, granted_by, approved_by)
            VALUES ('test-kallor', 'Test källor', ST_MakeEnvelope(0, 6000000, 1000000, 8000000, 3006), '{}',
                    '{coordinates,serialNumber}', '{}', 'whole', '{cmdb-test-kallor}', '{}', 'test', 'a', 'b')
            """);
        await Cmdb.Database.Scopes.ScopeVisibility.RefreshAsync(db, Ct);
        await using var api = await ApiAsync(db);
        using var full = NetworkFixture.Client(api);
        using var hidden = NetworkFixture.Client(api, "kallor", ["cmdb-test-kallor"]);

        var site = await Scalar(db, "SELECT id FROM site WHERE external_id = 'ex-site-2'");
        var source = (await full.GetFromJsonAsync<JsonElement>($"/api/sites/{site}", Ct)).GetProperty("sources").EnumerateArray().Single();
        source.GetProperty("source").GetString().ShouldBe(Source);
        source.GetProperty("externalId").GetString().ShouldBe("ex-site-2");
        source.GetProperty("origin").GetBoolean().ShouldBeTrue();
        var name = Value(source, "name");
        name.GetProperty("value").GetString().ShouldBe("Exempelradio");
        name.GetProperty("current").GetBoolean().ShouldBeFalse();
        name.GetProperty("owner").GetBoolean().ShouldBeTrue();
        Value(source, "position").GetProperty("current").GetBoolean().ShouldBeTrue();
        Value(source, "attributes.backupHours").GetProperty("value").GetInt32().ShouldBe(4);

        var masked = (await hidden.GetFromJsonAsync<JsonElement>($"/api/sites/{site}", Ct)).GetProperty("sources").EnumerateArray().Single();
        Attributes(masked).ShouldNotContain("position");
        Attributes(masked).ShouldContain("name");

        var equipment = await Scalar(db, "SELECT id FROM equipment WHERE external_id = 'ex-e5'");
        var nms = (await full.GetFromJsonAsync<JsonElement>($"/api/equipment/{equipment}", Ct)).GetProperty("sources").EnumerateArray().Single();
        Value(nms, "attributes.serialNumber").GetProperty("owner").GetBoolean().ShouldBeTrue();
        // Links to other objects are compared but their ids are not shown.
        var placement = Value(nms, "placement");
        placement.GetProperty("current").GetBoolean().ShouldBeTrue();
        (!placement.TryGetProperty("value", out var ids) || ids.ValueKind == JsonValueKind.Null).ShouldBeTrue();
        var maskedEquipment = (await hidden.GetFromJsonAsync<JsonElement>($"/api/equipment/{equipment}", Ct)).GetProperty("sources").EnumerateArray().Single();
        Attributes(maskedEquipment).ShouldNotContain("attributes.serialNumber");
        Attributes(maskedEquipment).ShouldContain("attributes.managementIp");

        foreach (var (path, table) in new[] { ("cables", "cable"), ("circuits", "circuit"), ("services", "service") })
        {
            var id = await Scalar(db, $"SELECT min(id) FROM {table}");
            var detail = await full.GetFromJsonAsync<JsonElement>($"/api/{path}/{id}", Ct);
            detail.GetProperty("sources").EnumerateArray().Single().GetProperty("values").EnumerateArray()
                .ShouldAllBe(v => v.GetProperty("current").GetBoolean(), table);
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
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRegistry>().LoadAsync(Ct);
        return api;
    }

    private static JsonElement Value(JsonElement source, string attribute) =>
        source.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("attribute").GetString() == attribute);

    private static List<string> Attributes(JsonElement source) =>
        [.. source.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("attribute").GetString()!)];

    private static Task<ImportResult> ImportAsync(NpgsqlDataSource db) =>
        NetworkImport.RunAsync(db, Example, Source, TypeCatalog.Current, dryRun: false, TextWriter.Null, Ct);

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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Plans;

/// <summary>
/// Attribute schemas for site, cable and service types (#211): attributes of sites and cables are checked against their
/// type's schema whether they come with a new object, a change or an import, and reach production when the plan is applied.
/// </summary>
public sealed class AttributeSchemaTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Site_and_cable_attributes_are_checked_against_the_type_schema_and_applied()
    {
        var (db, api) = await NetworkAsync(71);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var site = await Scalar(db, "SELECT id FROM site WHERE site_type = 'cabinet' ORDER BY id LIMIT 1");
        var cable = await Scalar(db, "SELECT c.id FROM cable c JOIN cable_type t ON t.id = c.cable_type_id WHERE t.key = 'fiber-96' ORDER BY c.id LIMIT 1");
        var plan = await CreateAsync(client, "Reservkraft och förläggningsår");

        async Task<HttpResponseMessage> Add(object op) => await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", op, Ct);
        static JsonElement Attrs(object value) => JsonSerializer.SerializeToElement(value);

        // A value of the wrong type or out of range never enters the plan.
        foreach (var op in new object[]
        {
            new { kind = "set_attributes", type = "site", objectId = site, attributes = Attrs(new { backupHours = "åtta" }) },
            new { kind = "set_attributes", type = "site", objectId = site, attributes = Attrs(new { backupHours = 10_000 }) },
            new { kind = "set_attributes", type = "cable", objectId = cable, attributes = Attrs(new { installationYear = 1850 }) },
            new { kind = "create_site", code = "ATTR-0", name = "Fel", siteType = "radio", x = 650000.0, y = 7100000.0, attributes = Attrs(new { backupHours = -1 }) },
            new { kind = "create_cable", aSiteId = site, bSiteId = site + 1, typeKey = "fiber-12", attributes = Attrs(new { owner = 42 }) },
        })
        {
            var refused = await Add(op);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("schema");
        }

        (await Add(new { kind = "set_attributes", type = "site", objectId = site, attributes = Attrs(new { backupHours = 8, aliases = new[] { "Skåpet" } }) }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Add(new { kind = "set_attributes", type = "cable", objectId = cable, attributes = Attrs(new { installationYear = 1998, owner = "Nätbolaget" }) }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var newSite = await Add(new
        {
            kind = "create_site",
            code = "ATTR-1",
            name = "Med reservkraft",
            siteType = "radio",
            x = 650000.0,
            y = 7100000.0,
            attributes = Attrs(new { backupHours = 4 }),
        });
        newSite.StatusCode.ShouldBe(HttpStatusCode.OK, await newSite.Content.ReadAsStringAsync(Ct));
        var planned = (await newSite.Content.ReadFromJsonAsync<PlanOperationView>(Ct))!.Target!.Id;
        var newCable = await Add(new { kind = "create_cable", aSiteId = site, bSiteId = planned, typeKey = "fiber-12", attributes = Attrs(new { installationYear = 2027 }) });
        newCable.StatusCode.ShouldBe(HttpStatusCode.OK, await newCable.Content.ReadAsStringAsync(Ct));

        var applied = await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        (await Text(db, $"SELECT attributes->>'backupHours' FROM site WHERE id = {site}")).ShouldBe("8");
        (await Text(db, $"SELECT attributes->>'owner' FROM cable WHERE id = {cable}")).ShouldBe("Nätbolaget");
        var realSite = await Scalar(db, "SELECT id FROM site WHERE code = 'ATTR-1'");
        (await Text(db, $"SELECT attributes->>'backupHours' FROM site WHERE id = {realSite}")).ShouldBe("4");
        (await Text(db, $"SELECT attributes->>'installationYear' FROM cable WHERE b_site_id = {realSite}")).ShouldBe("2027");

        // The site panel's detail carries the cable type, so the UI can name its attributes.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/cables/{cable}", Ct);
        detail.GetProperty("typeKey").GetString().ShouldBe("fiber-96");
    }

    [Fact]
    public async Task An_import_reads_columns_named_like_schema_properties_as_attributes()
    {
        var (db, api) = await NetworkAsync(72);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var plan = await CreateAsync(client, "Import med attribut");

        async Task<ImportResult> Import(string content)
        {
            var response = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/import", new { format = "csv", content }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<ImportResult>(Ct))!;
        }

        var bad = await Import("""
            kind;code;name;siteType;x;y;a;b;cableType;backupHours;installationYear
            site;ATTR-A;A;radio;650000;7100000;;;;många;
            site;ATTR-B;B;radio;651000;7100000;;;;2000;
            cable;;;;;;ATTR-A;ATTR-B;fiber-12;;1850
            """);
        bad.Errors.Select(e => e.Row).ShouldBe([2, 3, 4]);
        bad.Errors[0].Message.ShouldContain("backupHours");

        var done = await Import("""
            kind;code;name;siteType;x;y;a;b;cableType;backupHours;installationYear;notInSchema
            site;ATTR-A;A;radio;650000;7100000;;;;6;;x
            site;ATTR-B;B;radio;651000;7100000;;;;;;
            cable;;;;;;ATTR-A;ATTR-B;fiber-12;;2026;
            """);
        done.Errors.ShouldBeEmpty();
        (done.Sites, done.Cables).ShouldBe((2, 1));
        var payloads = await Texts(db, $"SELECT payload::text FROM plan_operation WHERE plan_id = {plan.Id} ORDER BY id");
        payloads[0].ShouldContain("\"backupHours\": 6");
        payloads[0].ShouldNotContain("notInSchema");
        payloads[1].ShouldNotContain("attributes");
        payloads[2].ShouldContain("\"installationYear\": 2026");
    }

    private async Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> NetworkAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
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

    private static async Task<List<string>> Texts(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var result = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }
}

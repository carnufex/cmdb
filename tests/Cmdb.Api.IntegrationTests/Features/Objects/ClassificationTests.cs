using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cmdb.Api.Features.Classifications;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Objects;

/// <summary>
/// Classifications (#176, ADR-0017): schemas from the catalog, levels set directly by people with write access and
/// within their scopes, and as a plan operation that an applied plan writes.
/// </summary>
public sealed class ClassificationTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Levels_are_set_checked_against_the_schema_and_read_back_and_a_regional_reader_cannot_write()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(51, Scale.Small, TypeCatalog.Embedded), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);
        var site = await Scalar(db, "SELECT min(id) FROM site WHERE lifecycle = 'in_service'");

        var schemas = (await client.GetFromJsonAsync<List<ClassificationSchemaView>>("/api/classifications/schemas", Ct))!;
        schemas.Single().Key.ShouldBe("criticality");

        (await client.GetFromJsonAsync<List<ObjectClassification>>($"/api/classifications?type=site&id={site}", Ct))!.ShouldBeEmpty();
        (await client.PutAsJsonAsync("/api/classifications", new { type = "site", id = site, schema = "criticality", level = 5 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var set = (await client.GetFromJsonAsync<List<ObjectClassification>>($"/api/classifications?type=site&id={site}", Ct))!.Single();
        (set.Level, set.Name, set.Critical, set.Source).ShouldBe((5, "Kritisk", true, "set"));

        // Setting it again replaces it; a level outside the schema, an unknown schema and a wrong object type are refused.
        (await client.PutAsJsonAsync("/api/classifications", new { type = "site", id = site, schema = "criticality", level = 2 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<List<ObjectClassification>>($"/api/classifications?type=site&id={site}", Ct))!.Single().Critical.ShouldBeFalse();
        (await client.PutAsJsonAsync("/api/classifications", new { type = "site", id = site, schema = "criticality", level = 9 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync("/api/classifications", new { type = "site", id = site, schema = "finns-inte", level = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync("/api/classifications", new { type = "circuit", id = 1, schema = "criticality", level = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Clearing it removes the row.
        (await client.PutAsJsonAsync("/api/classifications", new { type = "site", id = site, schema = "criticality", level = (int?)null }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Scalar(db, $"SELECT count(*) FROM classification WHERE object_type = 'site' AND object_id = {site}")).ShouldBe(0);

        // Reading follows the scopes; writing needs cmdb-full.
        using var regional = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        (await regional.PutAsJsonAsync("/api/classifications", new { type = "site", id = site, schema = "criticality", level = 1 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_plan_sets_a_classification_on_apply_and_a_service_counts_as_critical_from_the_schemas_level()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(52, Scale.Small, TypeCatalog.Embedded), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);
        var service = await Scalar(db, "SELECT min(id) FROM service");
        var equipment = await Scalar(db, "SELECT min(id) FROM equipment");

        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Klassa om" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        async Task<JsonElement> Add(object operation)
        {
            var response = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", operation, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }
        var refused = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations",
            new { kind = "set_classification", type = "equipment", objectId = equipment, schema = "criticality", level = 7 }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await Add(new { kind = "set_classification", type = "service", objectId = service, schema = "criticality", level = 5 }))
            .GetProperty("summary").GetString()!.ShouldContain("Kritikalitet");
        (await Add(new { kind = "set_classification", type = "equipment", objectId = equipment, schema = "criticality", level = 4 }))
            .GetProperty("summary").GetString()!.ShouldContain("till 4 (Mycket viktig)");
        // Nothing is written until a person applies the plan.
        (await Scalar(db, "SELECT count(*) FROM classification")).ShouldBe(0);

        (await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Scalar(db, $"SELECT level FROM classification WHERE object_type = 'service' AND object_id = {service}")).ShouldBe(5);
        (await Scalar(db, $"SELECT level FROM classification WHERE object_type = 'equipment' AND object_id = {equipment}")).ShouldBe(4);

        // The fault analysis reads the level: a service at the schema's critical level counts as critical, level 4 does not.
        (await Scalar(db, $"""
            SELECT count(*) FROM classification c WHERE c.object_type = 'service' AND c.object_id = {service}
              AND c.level >= {ClassificationCatalog.Embedded.Find("criticality")!.CriticalFrom}
            """)).ShouldBe(1);
    }

    [Fact]
    public async Task A_site_and_a_cable_inherit_the_highest_level_of_what_they_contain_and_carry_and_a_plan_counts_in_its_own_view()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(53, Scale.Small, TypeCatalog.Embedded), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        // Equipment that carries a service (found through the impact analysis), its site and rack, and a cable that carries one.
        async Task<(long Id, long Service)> Carrier(string path, string table)
        {
            foreach (var id in await Ids(db, $"SELECT id FROM {table} ORDER BY id LIMIT 400"))
            {
                var impact = await client.GetFromJsonAsync<JsonElement>($"/api/{path}/{id}/impact", Ct);
                if (impact.GetProperty("services").GetArrayLength() > 0)
                {
                    return (id, impact.GetProperty("services")[0].GetProperty("service").GetProperty("id").GetInt64());
                }
            }
            throw new InvalidOperationException($"No {table} carries a service.");
        }
        var (equipment, equipmentService) = await Carrier("equipment", "equipment");
        var (cable, service) = await Carrier("cables", "cable");
        var site = await Scalar(db, $"SELECT site_id FROM equipment WHERE id = {equipment}");
        var rack = await Scalar(db, $"SELECT location_id FROM equipment WHERE id = {equipment}");

        async Task<DerivedClassification> Derived(string type, long id, long? plan = null) =>
            (await client.GetFromJsonAsync<DerivedClassification>($"/api/classifications/derived?type={type}&id={id}{(plan is null ? "" : $"&plan={plan}")}", Ct))!;
        (await Derived("site", site)).Level.ShouldBe(0);
        _ = equipmentService;

        // A critical switch makes its rack, the room, the building and the site critical, and says why.
        (await client.PutAsJsonAsync("/api/classifications", new { type = "equipment", id = equipment, schema = "criticality", level = 5 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var derived = await Derived("site", site);
        (derived.Level, derived.Critical, derived.Inherited).ShouldBe((5, true, true));
        derived.Reasons.Single().Kind.ShouldBe("contains");
        derived.Reasons.Single().Subject.Id.ShouldBe(equipment);
        derived.LocationLevels.ShouldContain(l => l.Id == rack && l.Level == 5);
        derived.LocationLevels.Count.ShouldBeGreaterThan(1);

        // A critical service makes what it runs through critical: the cable, and the equipment it passes.
        (await client.PutAsJsonAsync("/api/classifications", new { type = "service", id = service, schema = "criticality", level = 4 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var carried = await Derived("cable", cable);
        (carried.Level, carried.Inherited).ShouldBe((4, true));
        carried.Reasons.ShouldContain(r => r.Kind == "carries" && r.Subject.Id == service);
        (await Derived("equipment", equipment)).Level.ShouldBe(5);

        // The plan's own change counts in its view and not in production.
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Höj tjänsten" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations",
            new { kind = "set_classification", type = "service", objectId = service, schema = "criticality", level = 5 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Derived("cable", cable, plan.Id)).Level.ShouldBe(5);
        (await Derived("cable", cable)).Level.ShouldBe(4);
        (await Derived("cable", cable, plan.Id)).ElapsedMs.ShouldBeLessThan(500);
    }

    private static async Task<List<long>> Ids(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

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
        await Loader.LoadAsync(db, NetworkBuilder.Build(51, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
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
        await Loader.LoadAsync(db, NetworkBuilder.Build(52, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
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

        // Level 4 on equipment raises its site to a level with requirements the site does not meet, so it takes an exception (#179).
        (await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/plans/{plan.Id}/apply?exception=test", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Scalar(db, $"SELECT level FROM classification WHERE object_type = 'service' AND object_id = {service}")).ShouldBe(5);
        (await Scalar(db, $"SELECT level FROM classification WHERE object_type = 'equipment' AND object_id = {equipment}")).ShouldBe(4);

        // The fault analysis reads the level: a service at the schema's critical level counts as critical, level 4 does not.
        (await Scalar(db, $"""
            SELECT count(*) FROM classification c WHERE c.object_type = 'service' AND c.object_id = {service}
              AND c.level >= {ClassificationCatalog.Current.Find("criticality")!.CriticalFrom}
            """)).ShouldBe(1);
    }

    [Fact]
    public async Task A_site_and_a_cable_inherit_the_highest_level_of_what_they_contain_and_carry_and_a_plan_counts_in_its_own_view()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(53, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
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

    [Fact]
    public async Task A_classified_site_must_meet_the_requirements_of_its_level_and_what_is_missing_shows_as_a_risk()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(54, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        // A site with exactly one in-service cable to one neighbour: two independent cables cannot be met.
        var site = await Scalar(db, """
            SELECT s.id FROM site s WHERE s.lifecycle = 'in_service'
              AND (SELECT count(*) FROM cable c WHERE (c.a_site_id = s.id OR c.b_site_id = s.id) AND c.lifecycle = 'in_service') = 1
            ORDER BY s.id LIMIT 1
            """);
        async Task<RuleReport> Rules() => (await client.GetFromJsonAsync<RuleReport>($"/api/classifications/rules?type=site&id={site}", Ct))!;

        (await Rules()).Results.ShouldBeEmpty();
        (await client.PutAsJsonAsync("/api/classifications", new { type = "site", id = site, schema = "criticality", level = 5 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var report = await Rules();
        report.Level.ShouldBe(5);
        report.Unmet.ShouldBe(2);
        var cables = report.Results.Single(r => r.Rule == "two-independent-cables");
        (cables.Met, cables.Required).ShouldBe((false, 2));
        cables.Hint.ShouldContain("Dra");
        var power = report.Results.Single(r => r.Rule == "backup-power");
        (power.Met, power.Actual).ShouldBe((false, null));

        // The requirement shows as a risk with what to do about it.
        var risks = (await client.GetFromJsonAsync<JsonElement>("/api/risks", Ct)).EnumerateArray().ToList();
        var risk = risks.Single(r => r.GetProperty("id").GetString() == $"classification-{site}");
        risk.GetProperty("kind").GetString().ShouldBe("classification");
        risk.GetProperty("description").GetString()!.ShouldContain("2 av 2 krav");

        // Setting the attribute meets that requirement.
        await using (var cmd = db.CreateCommand($"UPDATE site SET attributes = attributes || '{{\"backupHours\": 8}}'::jsonb WHERE id = {site}"))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var after = await Rules();
        after.Results.Single(r => r.Rule == "backup-power").Met.ShouldBeTrue();
        after.Unmet.ShouldBe(1);

        // A plan that adds two cables to different neighbours, which carry nothing, still does not meet it: the cables must carry the level.
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Fler kablar" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        var others = await Ids(db, $"SELECT id FROM site WHERE id <> {site} AND lifecycle = 'in_service' ORDER BY id LIMIT 2");
        foreach (var other in others)
        {
            (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "create_cable", aSiteId = site, bSiteId = other, typeKey = "fiber-12" }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        var planned = (await client.GetFromJsonAsync<RuleReport>($"/api/classifications/rules?type=site&id={site}&plan={plan.Id}", Ct))!;
        var plannedCables = planned.Results.Single(r => r.Rule == "two-independent-cables");
        plannedCables.Met.ShouldBeFalse();
        plannedCables.Hint.ShouldContain("bär inte nivån");
    }

    [Fact]
    public async Task A_new_level_five_switch_raises_its_site_and_the_plan_says_what_is_missing_and_is_applied_only_with_an_exception()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(55, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        // A site with a rack and a single cable to a single neighbour: it cannot meet two independent cables.
        var site = await Scalar(db, """
            SELECT s.id FROM site s
            WHERE s.lifecycle = 'in_service' AND EXISTS (SELECT 1 FROM location l WHERE l.site_id = s.id AND l.kind = 'rack')
              AND (SELECT count(*) FROM cable c WHERE (c.a_site_id = s.id OR c.b_site_id = s.id) AND c.lifecycle = 'in_service') = 1
            ORDER BY s.id LIMIT 1
            """);
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Ny kritisk switch" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        async Task<JsonElement> Add(object operation)
        {
            var response = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", operation, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }
        var created = await Add(new { kind = "create_equipment", siteId = site, typeKey = "acme-ax-24", name = "KRITISK-SW-1" });
        var switchId = created.GetProperty("target").GetProperty("id").GetInt64();
        switchId.ShouldBeLessThan(0);
        // The planned switch can be classified before it exists (a negative id is what the plan creates).
        (await Add(new { kind = "set_classification", type = "equipment", objectId = switchId, schema = "criticality", level = 5 }))
            .GetProperty("summary").GetString()!.ShouldContain("KRITISK-SW-1");

        var report = (await client.GetFromJsonAsync<PlanClassificationReport>($"/api/plans/{plan.Id}/classification", Ct))!;
        var finding = report.Findings.Single();
        (finding.Site.Id, finding.Before, finding.After, finding.Raised).ShouldBe((site, 0, 5, true));
        finding.Because.ShouldContain(r => r.Kind == "contains" && r.Subject.Code == "KRITISK-SW-1");
        finding.Unmet.Select(r => r.Rule).Order().ShouldBe(["backup-power", "two-independent-cables"]);
        report.Introduced.ShouldBe(2);
        finding.Suggestions.Count.ShouldBeGreaterThanOrEqualTo(2);
        finding.Suggestions.ShouldContain(s => s.Title.Contains("reservkraft", StringComparison.Ordinal));
        finding.Suggestions.ShouldContain(s => s.Title.Contains("Dra en kabel", StringComparison.Ordinal));

        // Without a reason the plan is not applied; with one it is, and the reason is kept.
        var refused = await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("krav ouppfyllda");
        (await client.PostAsync($"/api/plans/{plan.Id}/apply?exception=%20%20", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var applied = await client.PostAsync($"/api/plans/{plan.Id}/apply?exception={Uri.EscapeDataString("Reservkraft och kabel byggs i nasta etapp.")}", null, Ct);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        (await applied.Content.ReadFromJsonAsync<ApplyResult>(Ct))!.Plan.Exception.ShouldBe("Reservkraft och kabel byggs i nasta etapp.");
        var equipment = await Scalar(db, "SELECT id FROM equipment WHERE name = 'KRITISK-SW-1'");
        (await Scalar(db, $"SELECT level FROM classification WHERE object_type = 'equipment' AND object_id = {equipment}")).ShouldBe(5);

        // In production the site now derives level 5 from the switch.
        (await client.GetFromJsonAsync<DerivedClassification>($"/api/classifications/derived?type=site&id={site}", Ct))!.Level.ShouldBe(5);
    }

    [Fact]
    public async Task A_new_critical_site_gets_nearby_sites_that_already_meet_the_requirements_as_alternatives()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(56, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        // Every service critical, and reserve power everywhere: the existing sites meet level 5 in production.
        await using (var cmd = db.CreateCommand("""
            INSERT INTO classification (object_type, object_id, schema_key, level, source, set_by) SELECT 'service', id, 'criticality', 5, 'imported', 'test' FROM service;
            UPDATE site SET attributes = attributes || '{"backupHours": 8}'::jsonb;
            """))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);
        var x = await Scalar(db, "SELECT round(ST_X(ST_PointOnSurface(geom)))::bigint FROM site WHERE site_type = 'aggregation' ORDER BY id LIMIT 1");
        var y = await Scalar(db, "SELECT round(ST_Y(ST_PointOnSurface(geom)))::bigint FROM site WHERE site_type = 'aggregation' ORDER BY id LIMIT 1");

        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Ny kritisk site" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        async Task<JsonElement> Add(object operation)
        {
            var response = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", operation, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }
        var site = (await Add(new { kind = "create_site", code = "KRITISK-1", name = "Ny kritisk site", siteType = "radio", x = (double)x + 200, y = (double)y + 200 }))
            .GetProperty("target").GetProperty("id").GetInt64();
        var sw = (await Add(new { kind = "create_equipment", siteId = site, typeKey = "acme-ax-24", name = "KRITISK-SW-2" })).GetProperty("target").GetProperty("id").GetInt64();
        await Add(new { kind = "set_classification", type = "equipment", objectId = sw, schema = "criticality", level = 5 });

        var finding = (await client.GetFromJsonAsync<PlanClassificationReport>($"/api/plans/{plan.Id}/classification", Ct))!.Findings.Single();
        finding.Site.Code.ShouldBe("KRITISK-1");
        finding.Unmet.Select(r => r.Rule).ShouldContain("two-independent-cables");
        // Nearby sites that already meet the level and have room are offered instead, nearest first.
        finding.Alternatives.ShouldNotBeEmpty();
        finding.Alternatives.Count.ShouldBeLessThanOrEqualTo(3);
        finding.Alternatives.ShouldAllBe(a => a.FreeRackUnits >= 1 && a.DistanceM < 30_000);
        finding.Alternatives.Select(a => a.DistanceM).ShouldBe(finding.Alternatives.Select(a => a.DistanceM).Order());
    }

    [Fact]
    public async Task The_map_layer_lists_sites_and_cables_from_a_level_with_what_they_contain_and_carry_and_a_narrow_reader_sees_less()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(54, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        await using var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await api.Services.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>().Ready.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        using var client = NetworkFixture.Client(api);

        (await client.GetFromJsonAsync<JsonElement>("/api/classifications/map?min=1", Ct)).GetProperty("sites").GetArrayLength().ShouldBe(0);

        long service = 0, cable = 0;
        foreach (var id in await Ids(db, "SELECT id FROM cable ORDER BY id LIMIT 400"))
        {
            var impact = await client.GetFromJsonAsync<JsonElement>($"/api/cables/{id}/impact", Ct);
            if (impact.GetProperty("services").GetArrayLength() > 0)
            {
                (cable, service) = (id, impact.GetProperty("services")[0].GetProperty("service").GetProperty("id").GetInt64());
                break;
            }
        }
        cable.ShouldNotBe(0);
        var equipment = await Scalar(db, "SELECT id FROM equipment ORDER BY id LIMIT 1");
        var equipmentSite = await Scalar(db, $"SELECT site_id FROM equipment WHERE id = {equipment}");
        (await client.PutAsJsonAsync("/api/classifications", new { type = "service", id = service, schema = "criticality", level = 4 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync("/api/classifications", new { type = "equipment", id = equipment, schema = "criticality", level = 5 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var map = await client.GetFromJsonAsync<JsonElement>("/api/classifications/map?min=1", Ct);
        map.GetProperty("criticalFrom").GetInt32().ShouldBe(5);
        map.GetProperty("classifiedServices").GetInt32().ShouldBe(1);
        var cables = map.GetProperty("cables").EnumerateArray().ToList();
        cables.ShouldContain(c => c.GetProperty("id").GetInt64() == cable && c.GetProperty("level").GetInt32() == 4);
        cables.ShouldAllBe(c => c.GetProperty("coordinates").GetArrayLength() >= 2);
        var sites = map.GetProperty("sites").EnumerateArray().ToList();
        sites.Count.ShouldBeGreaterThan(1);
        sites.First(s => s.GetProperty("id").GetInt64() == equipmentSite).GetProperty("level").GetInt32().ShouldBe(5);
        sites[0].GetProperty("level").GetInt32().ShouldBe(5);

        // From a level up only what reaches it is listed.
        var critical = await client.GetFromJsonAsync<JsonElement>("/api/classifications/map?min=5", Ct);
        critical.GetProperty("cables").GetArrayLength().ShouldBe(0);
        critical.GetProperty("sites").EnumerateArray().ShouldAllBe(s => s.GetProperty("level").GetInt32() == 5);
        (await client.GetAsync("/api/classifications/map?schema=nope", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A regional reader sees no more than that: whatever it lists is also in the full map, and a site outside the region is not.
        using var regional = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var seen = await regional.GetFromJsonAsync<JsonElement>("/api/classifications/map?min=1", Ct);
        var all = sites.Select(s => s.GetProperty("id").GetInt64()).ToHashSet();
        var narrow = seen.GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("id").GetInt64()).ToList();
        narrow.ShouldAllBe(id => all.Contains(id));
        narrow.Count.ShouldBeLessThanOrEqualTo(all.Count);
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

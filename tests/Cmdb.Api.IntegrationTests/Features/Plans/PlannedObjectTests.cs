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
/// Plans that create sites, equipment and cables (#107): planned ids in the plan's view, traces through planned objects,
/// and applying them into production with the ids rewritten, also in plans that build on the applied one.
/// </summary>
public sealed class PlannedObjectTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_fibre_to_a_new_site_is_traced_in_the_plan_and_built_on_apply()
    {
        var (db, api) = await NetworkAsync(41);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var (site, port) = await SiteWithFreePortAsync(db);

        var plan = await CreateAsync(client, "Ny fiber till ny radiosite");
        var newSite = await AddAsync(client, plan.Id, new
        {
            kind = "create_site",
            code = "RAD-PLAN-1",
            name = "Planerad radiosite",
            siteType = "radio",
            x = 650000.0,
            y = 7100000.0,
        });
        newSite.Target!.Id.ShouldBeLessThan(0);
        var siteId = newSite.Target.Id;
        var radio = await AddAsync(client, plan.Id, new { kind = "create_equipment", siteId, typeKey = "acme-ax-24", name = "RAD-PLAN-1 AX-24 1" });
        radio.Summary.ShouldContain("RAD-PLAN-1 AX-24 1");
        var cable = await AddAsync(client, plan.Id, new { kind = "create_cable", aSiteId = site, bSiteId = siteId, typeKey = "fiber-12" });
        cable.Summary.ShouldContain("Fiberkabel 12");

        // Planned terminals: port n of the radio is -(op × 10000 + n); fibre k's ends are 2k-1 and 2k of the cable.
        var fibreA = -((cable.Id * 10_000) + 1);
        var fibreB = -((cable.Id * 10_000) + 2);
        var radioPort = -((radio.Id * 10_000) + 1);
        var splice = await AddAsync(client, plan.Id, new { kind = "connect", a = port, b = fibreA, connectionKind = "splice" });
        splice.Summary.ShouldContain("ledare 1 (A, planerad)");
        (await AddAsync(client, plan.Id, new { kind = "connect", a = fibreB, b = radioPort, connectionKind = "splice" })).Problem.ShouldBeNull();

        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={port}&plan={plan.Id}", Ct))!;
        trace.Physical!.Hops.Select(h => h.TerminalId).ShouldContain(radioPort);
        trace.Physical.Hops.Single(h => h.TerminalId == radioPort).Label.ShouldBe("RAD-PLAN-1 AX-24 1 · ge-0/0/1 (planerad)");
        var view = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        view.Problems.ShouldBe(0);
        view.Planned!.Sites.Single().Code.ShouldBe("RAD-PLAN-1");
        view.Planned.Cables.Single().Coordinates.Length.ShouldBe(2);

        // A second stage patches another port of the planned radio.
        var free = await FreePortsAsync(db, site, 2);
        var stage2 = await CreateAsync(client, "Etapp 2", plan.Id);
        var radioPort2 = -((radio.Id * 10_000) + 2);
        (await AddAsync(client, stage2.Id, new { kind = "connect", a = radioPort2, b = free[1], connectionKind = "patch" })).Problem.ShouldBeNull();

        var applied = await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        (await applied.Content.ReadFromJsonAsync<ApplyResult>(Ct))!.Flagged.ShouldBeEmpty();

        var realSite = await Scalar(db, "SELECT id FROM site WHERE code = 'RAD-PLAN-1'");
        (await Scalar(db, $"SELECT count(*) FROM equipment WHERE site_id = {realSite} AND name = 'RAD-PLAN-1 AX-24 1'")).ShouldBe(1);
        (await Scalar(db, $"SELECT count(*) FROM port p JOIN equipment e ON e.id = p.equipment_id WHERE e.site_id = {realSite}"))
            .ShouldBe(PortExpansion.Expand(TypeCatalog.Current.Find("acme-ax-24")!).Count);
        var realCable = await Scalar(db, $"SELECT id FROM cable WHERE a_site_id = {site} AND b_site_id = {realSite}");
        (await Scalar(db, $"SELECT count(*) FROM conductor WHERE cable_id = {realCable}")).ShouldBe(12);
        (await Scalar(db, $"SELECT count(*) FROM connection WHERE valid_to IS NULL AND {port} IN (a_terminal_id, b_terminal_id)")).ShouldBe(1);

        // The second stage now points at the real port, and production traces through the new fibre once the graph catches up.
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);
        var stage2Ops = (await client.GetFromJsonAsync<PlanDetail>($"/api/plans/{stage2.Id}", Ct))!.Operations;
        stage2Ops.Single().Terminals[0].TerminalId.ShouldBeGreaterThan(0);
        stage2Ops.Single().Problem.ShouldBeNull();
        var realRadioPort = await Scalar(db, $"""
            SELECT p.terminal_id FROM port p JOIN equipment e ON e.id = p.equipment_id WHERE e.site_id = {realSite} AND p.position = 1
            """);
        var production = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={port}", Ct))!.Physical!;
        production.Hops.Select(h => h.TerminalId).ShouldContain(realRadioPort);
    }

    [Fact]
    public async Task Creates_are_checked()
    {
        var (db, api) = await NetworkAsync(42);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var existingCode = await Text(db, "SELECT code FROM site ORDER BY id LIMIT 1");
        var plan = await CreateAsync(client, "Kontroller");

        async Task<HttpStatusCode> Add(object op) => (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", op, Ct)).StatusCode;

        (await Add(new { kind = "create_site", code = existingCode, name = "x", siteType = "radio", x = 600000.0, y = 7000000.0 })).ShouldBe(HttpStatusCode.BadRequest);
        (await Add(new { kind = "create_site", code = "NY-1", name = "x", siteType = "castle", x = 600000.0, y = 7000000.0 })).ShouldBe(HttpStatusCode.BadRequest);
        (await Add(new { kind = "create_site", code = "NY-1", name = "x", siteType = "radio", x = 99_000_000.0, y = 7000000.0 })).ShouldBe(HttpStatusCode.BadRequest);
        (await Add(new { kind = "create_equipment", siteId = -999L, typeKey = "acme-ax-24", name = "x" })).ShouldBe(HttpStatusCode.BadRequest);
        (await Add(new { kind = "create_equipment", siteId = 1L, typeKey = "no-such-model", name = "x" })).ShouldBe(HttpStatusCode.BadRequest);
        (await Add(new { kind = "create_cable", aSiteId = 1L, bSiteId = 1L, typeKey = "fiber-12" })).ShouldBe(HttpStatusCode.BadRequest);
        (await Add(new { kind = "create_cable", aSiteId = 1L, bSiteId = 2L, typeKey = "no-such-cable" })).ShouldBe(HttpStatusCode.BadRequest);
        (await Add(new { kind = "create_site", code = "NY-1", name = "Ny", siteType = "radio", x = 600000.0, y = 7000000.0 })).ShouldBe(HttpStatusCode.OK);
        (await Add(new { kind = "create_site", code = "NY-1", name = "Igen", siteType = "radio", x = 600000.0, y = 7000000.0 })).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_site_inserted_into_a_cable_keeps_every_service_running_and_frees_the_terminated_fibre()
    {
        var (db, api) = await NetworkAsync(43);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);

        // A cable with a circuit along one of its conductors and a conductor without any.
        var cable = await Scalar(db, """
            SELECT c.id FROM cable c
            WHERE c.lifecycle = 'in_service'
              AND EXISTS (SELECT 1 FROM conductor cd JOIN conductor_end e ON e.conductor_id = cd.id JOIN circuit_hop h ON h.terminal_id = e.terminal_id
                          WHERE cd.cable_id = c.id)
              AND EXISTS (SELECT 1 FROM conductor cd WHERE cd.cable_id = c.id AND NOT EXISTS (
                          SELECT 1 FROM conductor_end e JOIN circuit_hop h ON h.terminal_id = e.terminal_id WHERE e.conductor_id = cd.id))
            ORDER BY c.id LIMIT 1
            """);
        var code = await Text(db, $"SELECT code FROM cable WHERE id = {cable}");
        // The circuit along the cable; the services ride on it through the circuits above.
        var circuit = await Scalar(db, $"""
            SELECT min(h.circuit_id) FROM conductor cd JOIN conductor_end e ON e.conductor_id = cd.id JOIN circuit_hop h ON h.terminal_id = e.terminal_id
            WHERE cd.cable_id = {cable}
            """);
        var free = (int)await Scalar(db, $"""
            SELECT min(cd.number) FROM conductor cd WHERE cd.cable_id = {cable} AND NOT EXISTS (
                SELECT 1 FROM conductor_end e JOIN circuit_hop h ON h.terminal_id = e.terminal_id WHERE e.conductor_id = cd.id)
            """);
        var busy = (int)await Scalar(db, $"""
            SELECT min(cd.number) FROM conductor cd JOIN conductor_end e ON e.conductor_id = cd.id JOIN circuit_hop h ON h.terminal_id = e.terminal_id
            WHERE cd.cable_id = {cable}
            """);
        var x = await Scalar(db, $"SELECT round(ST_X(ST_LineInterpolatePoint(geom, 0.5)))::bigint FROM cable WHERE id = {cable}");
        var y = await Scalar(db, $"SELECT round(ST_Y(ST_LineInterpolatePoint(geom, 0.5)))::bigint FROM cable WHERE id = {cable}");
        // Placed on the cable's middle, the cable is suggested first, with the point to insert at (#169).
        var near = (await client.GetFromJsonAsync<List<Cmdb.Api.Features.Cables.NearCable>>($"/api/cables/near?x={x}&y={y}&radius=200", Ct))!;
        near[0].Cable.Id.ShouldBe(cable);
        near[0].Distance.ShouldBeLessThan(2);
        near[0].At.ShouldBe(0.5, 0.01);
        near[0].Free.ShouldBeGreaterThan(0);
        near[0].Free.ShouldBeLessThan(near[0].Conductors);
        var before = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?circuit={circuit}", Ct))!;
        before.Cables.Select(c => c.Id).ShouldContain(cable);

        var plan = await CreateAsync(client, "Ny skarvpunkt på kabeln");
        var site = (await AddAsync(client, plan.Id, new { kind = "create_site", code = "SKARV-PLAN-1", name = "Ny skarvpunkt", siteType = "splice", x = (double)x, y = (double)y }))
            .Target!.Id;

        // A conductor that carries a circuit cannot be terminated: the service would break.
        var refused = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations",
            new { kind = "split_cable", cableId = cable, siteId = site, terminate = new[] { busy } }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("bär kretsar");

        var split = await AddAsync(client, plan.Id, new { kind = "split_cable", cableId = cable, siteId = site, terminate = new[] { free } });
        split.Problem.ShouldBeNull();
        split.Summary.ShouldContain($"i kabel {code}");
        split.Summary.ShouldContain("1 termineras");

        var view = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        view.Problems.ShouldBe(0);
        view.Planned!.Cables.Select(c => c.Code).ShouldBe([$"{code}-A", $"{code}-B"]);
        view.Planned.Cables.ShouldAllBe(c => c.Coordinates.Length >= 2);
        view.Planned.Removed!.Cables.Select(c => c.Id).ShouldBe([cable]);

        // In the plan the circuit runs through the two new parts and the new site, not the old cable.
        var planned = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?circuit={circuit}&plan={plan.Id}", Ct))!;
        planned.Cables.Select(c => c.Id).ShouldNotContain(cable);
        planned.Cables.Select(c => c.Code).ShouldContain($"{code}-A");
        planned.Cables.Select(c => c.Code).ShouldContain($"{code}-B");
        planned.Services.Select(s => s.Id).ShouldBe(before.Services.Select(s => s.Id));

        var applied = await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct);
        applied.StatusCode.ShouldBe(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);

        (await Text(db, $"SELECT lifecycle::text FROM cable WHERE id = {cable}")).ShouldBe("removed");
        var partA = await Scalar(db, $"SELECT id FROM cable WHERE code = '{code}-A'");
        var partB = await Scalar(db, $"SELECT id FROM cable WHERE code = '{code}-B'");
        var realSite = await Scalar(db, "SELECT id FROM site WHERE code = 'SKARV-PLAN-1'");
        (await Scalar(db, $"SELECT b_site_id FROM cable WHERE id = {partA}")).ShouldBe(realSite);
        (await Scalar(db, $"SELECT a_site_id FROM cable WHERE id = {partB}")).ShouldBe(realSite);
        // Spliced through in the site, except the terminated one, which is free there.
        string Inner(long part, string side, int number) => $"""
            SELECT e.terminal_id FROM conductor cd JOIN conductor_end e ON e.conductor_id = cd.id
            WHERE cd.cable_id = {part} AND e.side = '{side}' AND cd.number = {number}
            """;
        (await Scalar(db, $"SELECT count(*) FROM connection WHERE valid_to IS NULL AND kind = 'splice' AND a_terminal_id IN (({Inner(partA, "B", busy)}), ({Inner(partB, "A", busy)})) AND b_terminal_id IN (({Inner(partA, "B", busy)}), ({Inner(partB, "A", busy)}))"))
            .ShouldBe(1);
        (await Scalar(db, $"SELECT count(*) FROM connection WHERE valid_to IS NULL AND (a_terminal_id = ({Inner(partA, "B", free)}) OR b_terminal_id = ({Inner(partA, "B", free)}))"))
            .ShouldBe(0);

        var after = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?circuit={circuit}", Ct))!;
        after.Cables.Select(c => c.Id).ShouldNotContain(cable);
        after.Cables.Select(c => c.Id).ShouldContain(partA);
        after.Cables.Select(c => c.Id).ShouldContain(partB);
        after.Sites.Select(s => s.Id).ShouldContain(realSite);
        after.Services.Select(s => s.Id).ShouldBe(before.Services.Select(s => s.Id));
    }

    [Fact]
    public async Task Removing_refuses_what_carries_circuits_and_takes_a_site_with_its_equipment_and_cables()
    {
        var (db, api) = await NetworkAsync(44);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);

        // Equipment that carries circuits cannot be removed: its services would break.
        var busy = await Scalar(db, """
            SELECT min(p.equipment_id) FROM port p JOIN circuit_hop h ON h.terminal_id = p.terminal_id
            """);
        var refusePlan = await CreateAsync(client, "Ta bort utrustning med kretsar");
        var refused = await client.PostAsJsonAsync($"/api/plans/{refusePlan.Id}/operations", new { kind = "remove", type = "equipment", objectId = busy }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("kretsar går genom");

        // A site built by one plan, with equipment and a cable patched to the network, and removed by the next.
        var (site, port) = await SiteWithFreePortAsync(db);
        var build = await CreateAsync(client, "Bygg en site");
        var newSite = (await AddAsync(client, build.Id, new { kind = "create_site", code = "RAD-BORT-1", name = "Tillfällig site", siteType = "radio", x = 651000.0, y = 7101000.0 }))
            .Target!.Id;
        var radio = await AddAsync(client, build.Id, new { kind = "create_equipment", siteId = newSite, typeKey = "acme-ax-24", name = "RAD-BORT-1 AX-24 1" });
        var cableOp = await AddAsync(client, build.Id, new { kind = "create_cable", aSiteId = site, bSiteId = newSite, typeKey = "fiber-12" });
        (await AddAsync(client, build.Id, new { kind = "connect", a = port, b = -((cableOp.Id * 10_000) + 1), connectionKind = "splice" })).Problem.ShouldBeNull();
        (await AddAsync(client, build.Id, new { kind = "connect", a = -((cableOp.Id * 10_000) + 2), b = -((radio.Id * 10_000) + 1), connectionKind = "splice" }))
            .Problem.ShouldBeNull();
        (await client.PostAsync($"/api/plans/{build.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);
        var realSite = await Scalar(db, "SELECT id FROM site WHERE code = 'RAD-BORT-1'");
        var realCable = await Scalar(db, $"SELECT id FROM cable WHERE b_site_id = {realSite}");
        var realRadio = await Scalar(db, $"SELECT id FROM equipment WHERE site_id = {realSite}");

        var remove = await CreateAsync(client, "Riv siten");
        var removal = await AddAsync(client, remove.Id, new { kind = "remove", type = "site", objectId = realSite });
        removal.Problem.ShouldBeNull();
        removal.Summary.ShouldContain("Ta bort site RAD-BORT-1");
        removal.Summary.ShouldContain("1 utrustningar och 1 kablar");
        (await client.PostAsJsonAsync($"/api/plans/{remove.Id}/operations", new { kind = "remove", type = "site", objectId = realSite }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var view = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{remove.Id}/view", Ct))!;
        view.Problems.ShouldBe(0);
        view.Planned!.Removed!.Cables.Select(c => c.Id).ShouldBe([realCable]);
        view.Planned.Removed.Sites.Select(s => s.Id).ShouldBe([realSite]);
        // In the plan the network's port no longer reaches the removed site.
        var trace = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={port}&plan={remove.Id}", Ct))!;
        trace.Physical!.Hops.Select(h => h.Site?.Id).ShouldNotContain(realSite);

        (await client.PostAsync($"/api/plans/{remove.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);
        (await Text(db, $"SELECT lifecycle::text FROM site WHERE id = {realSite}")).ShouldBe("removed");
        (await Text(db, $"SELECT lifecycle::text FROM equipment WHERE id = {realRadio}")).ShouldBe("removed");
        (await Text(db, $"SELECT lifecycle::text FROM cable WHERE id = {realCable}")).ShouldBe("removed");
        (await Scalar(db, $"""
            SELECT count(*) FROM connection WHERE valid_to IS NULL AND (a_terminal_id = {port} OR b_terminal_id = {port}) AND kind = 'splice'
            """)).ShouldBe(0);
        var after = (await client.GetFromJsonAsync<TraceResult>($"/api/trace?terminal={port}", Ct))!;
        after.Physical!.Hops.Select(h => h.Site?.Id).ShouldNotContain(realSite);
    }

    [Fact]
    public async Task An_import_checks_every_row_first_then_writes_sites_templates_and_cables_and_can_run_again()
    {
        var (db, api) = await NetworkAsync(45);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var existing = await Text(db, "SELECT code FROM site WHERE site_type = 'aggregation' ORDER BY id LIMIT 1");
        var plan = await CreateAsync(client, "Import av radiositer");

        async Task<ImportResult> Import(string format, string content, bool dryRun = false)
        {
            var response = await client.PostAsJsonAsync($"/api/plans/{plan.Id}/import", new { format, content, dryRun }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<ImportResult>(Ct))!;
        }

        // Every row is checked first; with any problem nothing is written.
        var bad = await Import("csv", $"""
            kind;code;name;template;siteType;lat;lon;a;b;cableType
            site;IMP-1;Import 1;radiosite-standard;;63.80;20.25;;;
            site;IMP-1;Dubblett;;radio;63.81;20.26;;;
            site;IMP-2;Okänd mall;finns-inte;;63.82;20.27;;;
            cable;;;;;;;IMP-1;FINNS-INTE;fiber-12
            """);
        bad.Errors.Select(e => e.Row).ShouldBe([3, 4, 5]);
        (await Scalar(db, $"SELECT count(*) FROM plan_operation WHERE plan_id = {plan.Id}")).ShouldBe(0);

        var csv = $"""
            kind,code,name,template,siteType,lat,lon,a,b,cableType
            site,IMP-1,Import 1,radiosite-standard,,63.80,20.25,,,
            site,IMP-2,Import 2,,radio,63.81,20.27,,,
            cable,,,,,,,IMP-1,IMP-2,fiber-12
            cable,,,,,,,IMP-1,{existing},fiber-12
            """;
        var dry = await Import("csv", csv, dryRun: true);
        (dry.Sites, dry.Cables, dry.Errors.Count).ShouldBe((2, 2, 0));
        (await Scalar(db, $"SELECT count(*) FROM plan_operation WHERE plan_id = {plan.Id}")).ShouldBe(0);

        var done = await Import("csv", csv);
        done.Problems.ShouldBe(0);
        var template = SiteTemplates.Current.Find("radiosite-standard")!;
        (done.Sites, done.Equipment, done.Connections, done.Cables).ShouldBe((2, template.Equipment.Count, template.Connections.Count, 2));
        var view = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        view.Problems.ShouldBe(0);
        view.Planned!.Sites.Select(s => s.Code).Order().ShouldBe(["IMP-1", "IMP-2"]);
        // WGS 84 became SWEREF 99 TM (Umeå is around 760 000, 7 078 000).
        view.Planned.Sites.ShouldAllBe(s => s.X > 700_000 && s.X < 800_000 && s.Y > 7_000_000 && s.Y < 7_150_000);

        // Running it again changes nothing: the plan has these sites and cables already.
        var again = await Import("csv", csv);
        (again.Sites, again.Cables, again.Skipped).ShouldBe((0, 0, 4));

        // GeoJSON: a line string is a cable along its own route.
        var geo = await Import("geojson", """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[761000,7079000]},"properties":{"code":"IMP-3","name":"Import 3","siteType":"cabinet"}},
              {"type":"Feature","geometry":{"type":"LineString","coordinates":[[760500,7078500],[760800,7078900]]},"properties":{"a":"IMP-2","b":"IMP-3","cableType":"fiber-12"}}
            ]}
            """);
        (geo.Sites, geo.Cables, geo.Errors.Count).ShouldBe((1, 1, 0));
        view = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        view.Planned!.Cables.Single(c => c.Coordinates.Length == 4).ShouldNotBeNull();

        // Two thousand sites with a template go in at once.
        var many = new System.Text.StringBuilder("code;name;template;x;y\n");
        for (var i = 0; i < 2000; i++)
        {
            many.Append(System.Globalization.CultureInfo.InvariantCulture, $"MASS-{i};Mass {i};radiosite-standard;{600_000 + (i % 100) * 500};{6_900_000 + (i / 100) * 500}\n");
        }
        var big = await Import("csv", many.ToString());
        big.Errors.ShouldBeEmpty();
        big.Sites.ShouldBe(2000);
        big.ElapsedMs.ShouldBeLessThan(30_000);
        // The plan's view lists the first operations and counts the rest; all=true lists every one.
        var bigView = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        var total = await Scalar(db, $"SELECT count(*) FROM plan_operation WHERE plan_id = {plan.Id}");
        bigView.Counts![plan.Id].ShouldBe((int)total);
        bigView.Changes.Count.ShouldBe(PlanViewEndpoint.Listed);
        (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view?all=true", Ct))!.Changes.Count.ShouldBe((int)total);
        TestContext.Current.SendDiagnosticMessage($"Import of 2000 templated sites: {big.ElapsedMs} ms");
    }

    [Fact]
    public async Task Equipment_goes_into_a_rack_position_that_fits_and_is_free()
    {
        var (db, api) = await NetworkAsync(46);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);

        // Loaded equipment is stacked in its racks (#173).
        var rack = await Scalar(db, """
            SELECT l.id FROM location l JOIN equipment e ON e.location_id = l.id
            WHERE l.kind = 'rack' AND e.rack_position IS NOT NULL GROUP BY l.id ORDER BY count(*) DESC, l.id LIMIT 1
            """);
        var site = await Scalar(db, $"SELECT site_id FROM location WHERE id = {rack}");
        var rackName = await Text(db, $"SELECT name FROM location WHERE id = {rack}");
        var taken = await Scalar(db, $"SELECT min(rack_position) FROM equipment WHERE location_id = {rack}");
        var top = await Scalar(db, $"""
            SELECT max(e.rack_position + t.rack_units) FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id WHERE e.location_id = {rack}
            """);
        var detail = (await client.GetFromJsonAsync<Cmdb.Api.Features.Sites.SiteDetail>($"/api/sites/{site}", Ct))!;
        var location = detail.Locations.Single(l => l.Id == rack);
        location.RackUnits.ShouldBe(42);
        location.Equipment.ShouldContain(e => e.Position == taken && e.Units == 1);

        var plan = await CreateAsync(client, "Ny switch i racket");
        async Task<HttpResponseMessage> Add(int? position) => await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations",
            new { kind = "create_equipment", siteId = site, typeKey = "acme-ax-24", name = $"SW-RACK-{position}", rack = rackName, position }, Ct);
        var clash = await Add((int)taken);
        clash.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await clash.Content.ReadAsStringAsync(Ct)).ShouldContain("krockar med");
        var tooHigh = await Add(42 + 1);
        (await tooHigh.Content.ReadAsStringAsync(Ct)).ShouldContain("Racket har 42 U");
        (await Add(40)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // The plan's own equipment takes its units too.
        (await Add(40)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Add(null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Scalar(db, $"SELECT rack_position FROM equipment WHERE name = 'SW-RACK-40'")).ShouldBe(40);
        // Without a position it goes on top of what the rack held, under the one placed at 40.
        (await Scalar(db, $"SELECT rack_position FROM equipment WHERE name = 'SW-RACK-'")).ShouldBe(System.Math.Max(top, 41));
    }

    [Fact]
    public async Task Equipment_and_cable_ends_move_between_racks_and_sites_and_what_carries_circuits_stays()
    {
        var (db, api) = await NetworkAsync(46);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);

        // Equipment that carries circuits cannot change site: its services would break.
        var busy = await Scalar(db, "SELECT min(p.equipment_id) FROM port p JOIN circuit_hop h ON h.terminal_id = p.terminal_id");
        var busySite = await Scalar(db, $"SELECT site_id FROM equipment WHERE id = {busy}");
        var other = await Scalar(db, $"SELECT min(id) FROM site WHERE id <> {busySite}");
        var refusePlan = await CreateAsync(client, "Flytta utrustning med kretsar");
        var refused = await client.PostAsJsonAsync($"/api/plans/{refusePlan.Id}/operations", new { kind = "move", type = "equipment", objectId = busy, siteId = other }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("kretsar går genom");

        // A site with a switch and a cable patched to the network.
        var (site, port) = await SiteWithFreePortAsync(db);
        var build = await CreateAsync(client, "Bygg en site");
        var newSite = (await AddAsync(client, build.Id, new { kind = "create_site", code = "RAD-FLYTT-1", name = "Flyttsite", siteType = "radio", x = 651000.0, y = 7101000.0 })).Target!.Id;
        var radio = await AddAsync(client, build.Id, new { kind = "create_equipment", siteId = newSite, typeKey = "acme-ax-24", name = "RAD-FLYTT-1 AX-24 1" });
        var cableOp = await AddAsync(client, build.Id, new { kind = "create_cable", aSiteId = site, bSiteId = newSite, typeKey = "fiber-12" });
        (await AddAsync(client, build.Id, new { kind = "connect", a = port, b = -((cableOp.Id * 10_000) + 1), connectionKind = "splice" })).Problem.ShouldBeNull();
        (await AddAsync(client, build.Id, new { kind = "connect", a = -((cableOp.Id * 10_000) + 2), b = -((radio.Id * 10_000) + 1), connectionKind = "splice" })).Problem.ShouldBeNull();
        (await client.PostAsync($"/api/plans/{build.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);
        var realSite = await Scalar(db, "SELECT id FROM site WHERE code = 'RAD-FLYTT-1'");
        var cable = await Scalar(db, $"SELECT id FROM cable WHERE b_site_id = {realSite}");
        var switchId = await Scalar(db, $"SELECT id FROM equipment WHERE site_id = {realSite}");
        var third = await Scalar(db, $"SELECT min(id) FROM site WHERE id NOT IN ({site}, {realSite})");
        var switchPort = await Scalar(db, $"SELECT min(terminal_id) FROM port WHERE equipment_id = {switchId}");
        var cableEnd = await Scalar(db, $"SELECT e.terminal_id FROM conductor_end e JOIN conductor k ON k.id = e.conductor_id WHERE k.cable_id = {cable} AND k.number = 1 AND e.side = 'B'");
        async Task<long> Live(long terminal) => await Scalar(db, $"SELECT count(*) FROM connection WHERE valid_to IS NULL AND ({terminal} IN (a_terminal_id, b_terminal_id))");
        (await Live(switchPort)).ShouldBe(1);
        (await Live(cableEnd)).ShouldBe(1);

        // Checked before they are added.
        var move = await CreateAsync(client, "Flytta");
        async Task<HttpResponseMessage> Try(object op) => await client.PostAsJsonAsync($"/api/plans/{move.Id}/operations", op, Ct);
        (await Try(new { kind = "move", type = "equipment", objectId = switchId, siteId = realSite })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Try(new { kind = "move", type = "cable", objectId = cable, siteId = site, end = "B" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Try(new { kind = "move", type = "cable", objectId = cable, siteId = third })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The switch goes to the other site, and the cable's far end to a third; the connections on them go.
        var movedSwitch = await AddAsync(client, move.Id, new { kind = "move", type = "equipment", objectId = switchId, siteId = site });
        movedSwitch.Problem.ShouldBeNull();
        movedSwitch.Summary.ShouldContain("Flytta utrustning");
        var movedEnd = await AddAsync(client, move.Id, new { kind = "move", type = "cable", objectId = cable, siteId = third, end = "B" });
        movedEnd.Problem.ShouldBeNull();
        movedEnd.Summary.ShouldContain("Flytta ände B av kabel");
        (await Try(new { kind = "move", type = "cable", objectId = cable, siteId = third, end = "B" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{move.Id}/view", Ct))!.Problems.ShouldBe(0);
        (await client.PostAsync($"/api/plans/{move.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);
        (await Scalar(db, $"SELECT site_id FROM equipment WHERE id = {switchId}")).ShouldBe(site);
        (await Scalar(db, $"SELECT b_site_id FROM cable WHERE id = {cable}")).ShouldBe(third);
        (await Scalar(db, $"SELECT a_site_id FROM cable WHERE id = {cable}")).ShouldBe(site);
        (await Scalar(db, $"SELECT CASE WHEN ST_Equals(ST_EndPoint(geom), (SELECT ST_PointOnSurface(geom) FROM site WHERE id = {third})) THEN 1 ELSE 0 END FROM cable WHERE id = {cable}")).ShouldBe(1);
        (await Live(switchPort)).ShouldBe(0);
        (await Live(cableEnd)).ShouldBe(0);
        // The connection at the end that stayed is still there.
        (await Scalar(db, $"SELECT count(*) FROM connection WHERE valid_to IS NULL AND kind = 'splice' AND {port} IN (a_terminal_id, b_terminal_id)")).ShouldBe(1);

        // Within a site only the rack and the position change.
        var rack = await CreateAsync(client, "Annat rack");
        (await AddAsync(client, rack.Id, new { kind = "move", type = "equipment", objectId = switchId, siteId = site, rack = "Rack Flytt", position = 10 })).Problem.ShouldBeNull();
        (await client.PostAsync($"/api/plans/{rack.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);
        (await Text(db, $"SELECT l.name FROM equipment e JOIN location l ON l.id = e.location_id WHERE e.id = {switchId}")).ShouldBe("Rack Flytt");
        (await Scalar(db, $"SELECT rack_position FROM equipment WHERE id = {switchId}")).ShouldBe(10);
        (await Scalar(db, $"SELECT site_id FROM equipment WHERE id = {switchId}")).ShouldBe(site);
    }

    [Fact]
    public async Task Locations_cards_services_circuits_and_their_links_are_planned_and_applied()
    {
        var (db, api) = await NetworkAsync(47);
        await using var dbScope = db;
        await using var apiScope = api;
        using var client = NetworkFixture.Client(api);
        var plan = await CreateAsync(client, "Nät från källsystem");

        // A room and a rack in it, on a hub.
        var hub = await Scalar(db, "SELECT min(id) FROM site WHERE site_type = 'hub'");
        var room = (await AddAsync(client, plan.Id, new { kind = "create_location", siteId = hub, locationKind = "room", name = "Rum 9" })).Target!.Id;
        var rack = await AddAsync(client, plan.Id, new { kind = "create_location", siteId = hub, parentId = room, locationKind = "rack", name = "Rack 9", rackUnits = 42 });
        rack.Summary.ShouldContain("Rack 9");

        // A card in a free slot of a chassis; a slot that is taken or missing is refused.
        var chassis = await Scalar(db, "SELECT min(e.id) FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id AND t.key = 'acme-cr-8'");
        var taken = await Text(db, $"SELECT min(slot) FROM equipment WHERE parent_id = {chassis}");
        var free = Enumerable.Range(1, 8).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).First(s => s != taken
            && Scalar(db, $"SELECT count(*) FROM equipment WHERE parent_id = {chassis} AND slot = '{s}'").Result == 0);
        (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "create_equipment", parentId = chassis, slot = taken, typeKey = "acme-lc-24x", name = "Kort X" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "create_equipment", parentId = chassis, slot = "99", typeKey = "acme-lc-24x", name = "Kort X" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var card = await AddAsync(client, plan.Id, new { kind = "create_equipment", parentId = chassis, slot = free, typeKey = "acme-lc-24x", name = "Kort X" });
        card.Problem.ShouldBeNull();

        // A service on a new circuit between two free ports, riding on an existing physical circuit.
        var (site, _) = await SiteWithFreePortAsync(db);
        var ports = await FreePortsAsync(db, site, 2);
        var carrier = await Scalar(db, "SELECT min(id) FROM circuit WHERE layer = 'physical'");
        var service = (await AddAsync(client, plan.Id, new { kind = "create_service", code = "TJ-KALLA-1", name = "Från källan", serviceType = "ethernet" })).Target!.Id;
        var circuit = (await AddAsync(client, plan.Id, new { kind = "create_circuit", code = "LOG-KALLA-1", layer = "logical", hops = ports })).Target!.Id;
        (await AddAsync(client, plan.Id, new { kind = "link_circuit", objectId = circuit, carrierId = carrier })).Problem.ShouldBeNull();
        (await AddAsync(client, plan.Id, new { kind = "link_service", objectId = service, circuitId = circuit })).Summary.ShouldContain("TJ-KALLA-1");
        (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "create_circuit", code = "LOG-KALLA-1", layer = "logical", hops = ports }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // An existing circuit's path changes: the same hops in reverse.
        var existing = await Scalar(db, "SELECT min(circuit_id) FROM circuit_hop");
        var path = (await Ids(db, $"SELECT terminal_id FROM circuit_hop WHERE circuit_id = {existing} ORDER BY seq")).AsEnumerable().Reverse().ToArray();
        (await AddAsync(client, plan.Id, new { kind = "set_circuit_path", objectId = existing, hops = path })).Problem.ShouldBeNull();

        var view = (await client.GetFromJsonAsync<PlanDiff>($"/api/plans/{plan.Id}/view", Ct))!;
        view.Problems.ShouldBe(0);
        (await client.PostAsync($"/api/plans/{plan.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApiFactory.GraphCaughtUpAsync(api.Services, db);

        var realRack = await Scalar(db, "SELECT id FROM location WHERE name = 'Rack 9'");
        (await Scalar(db, $"SELECT parent_id FROM location WHERE id = {realRack}")).ShouldBe(await Scalar(db, "SELECT id FROM location WHERE name = 'Rum 9'"));
        (await Scalar(db, $"SELECT count(*) FROM port p JOIN equipment e ON e.id = p.equipment_id WHERE e.parent_id = {chassis} AND e.slot = '{free}'")).ShouldBeGreaterThan(0);
        var realCircuit = await Scalar(db, "SELECT id FROM circuit WHERE code = 'LOG-KALLA-1'");
        (await Ids(db, $"SELECT terminal_id FROM circuit_hop WHERE circuit_id = {realCircuit} ORDER BY seq")).ShouldBe(ports);
        (await Scalar(db, $"SELECT count(*) FROM circuit_dependency WHERE circuit_id = {realCircuit} AND carrier_id = {carrier}")).ShouldBe(1);
        (await Scalar(db, $"SELECT count(*) FROM service_circuit sc JOIN service s ON s.id = sc.service_id WHERE s.code = 'TJ-KALLA-1' AND sc.circuit_id = {realCircuit}")).ShouldBe(1);
        (await Ids(db, $"SELECT terminal_id FROM circuit_hop WHERE circuit_id = {existing} ORDER BY seq")).ShouldBe(path);
        var trace = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/trace?service={await Scalar(db, "SELECT id FROM service WHERE code = 'TJ-KALLA-1'")}", Ct);
        trace.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
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

    private static async Task<PlanSummary> CreateAsync(HttpClient client, string name, params long[] dependsOn)
    {
        var response = await client.PostAsJsonAsync("/api/plans", new { name, dependsOn }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
    }

    private static async Task<PlanOperationView> AddAsync(HttpClient client, long plan, object operation)
    {
        var response = await client.PostAsJsonAsync($"/api/plans/{plan}/operations", operation, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PlanOperationView>(Ct))!;
    }

    /// <summary>A site with at least three unconnected ports, and the first of them.</summary>
    private static async Task<(long Site, long Port)> SiteWithFreePortAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("""
            SELECT e.site_id, min(p.terminal_id) FROM port p JOIN equipment e ON e.id = p.equipment_id
            WHERE NOT EXISTS (SELECT 1 FROM connection c WHERE p.terminal_id IN (c.a_terminal_id, c.b_terminal_id))
            GROUP BY e.site_id HAVING count(*) >= 3 ORDER BY e.site_id LIMIT 1
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<List<long>> FreePortsAsync(NpgsqlDataSource db, long site, int count)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT p.terminal_id FROM port p JOIN equipment e ON e.id = p.equipment_id
            WHERE e.site_id = {site} AND NOT EXISTS (SELECT 1 FROM connection c WHERE p.terminal_id IN (c.a_terminal_id, c.b_terminal_id))
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

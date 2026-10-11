using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Services;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Services;

/// <summary>
/// The service view (#257): a service's trace with the pictures of the equipment on it, the used ports placed on them,
/// and the cables' types and lengths, inside the caller's scope.
/// </summary>
public sealed class ServiceDrawingTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_service_is_drawn_with_its_layers_equipment_pictures_and_cables()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        // A service riding on a circuit that rides on a physical one: every layer is drawn.
        var service = await Scalar(db, """
            SELECT s.service_id FROM service_circuit s JOIN circuit_dependency d ON d.circuit_id = s.circuit_id
            JOIN circuit p ON p.id = d.carrier_id AND p.layer = 'physical'
            ORDER BY s.service_id LIMIT 1
            """);

        var (drawing, fastest) = await Budget.WarmAsync(
            async () => (await client.GetFromJsonAsync<ServiceDrawing>($"/api/services/{service}/drawing", Ct))!, d => d.ElapsedMs);
        drawing.Trace.Service!.Id.ShouldBe(service);
        drawing.Trace.Circuits.Select(c => c.Layer).ShouldContain("physical");
        drawing.Trace.Circuits.Select(c => c.Layer).Distinct().Count().ShouldBeGreaterThan(1);

        // Every port on the path is described with its equipment, and every cable with its type and length.
        var hops = drawing.Trace.Circuits.SelectMany(c => c.Hops).ToList();
        var ports = hops.Where(h => h.Kind == "port").Select(h => h.TerminalId).ToHashSet();
        drawing.Equipment.SelectMany(e => e.Ports).Select(p => p.TerminalId).ToHashSet().SetEquals(ports).ShouldBeTrue();
        drawing.Equipment.ShouldAllBe(e => e.Model.Length > 0 && e.TypeKey.Length > 0);
        var cables = hops.Where(h => h.Cable != null).Select(h => h.Cable!.Id).ToHashSet();
        drawing.Cables.Select(c => c.Id).ToHashSet().SetEquals(cables).ShouldBeTrue();
        drawing.Cables.ShouldAllBe(c => c.LengthM > 0 && c.Conductors > 0 && c.TypeName.Length > 0);
        // A used port with a place on its model's picture comes with that picture's side.
        foreach (var e in drawing.Equipment.Where(e => e.Ports.Any(p => p.Box != null)))
        {
            e.Image.ShouldNotBeNull();
        }
        fastest.ShouldBeLessThan(50);

        (await client.GetAsync("/api/services/999999999/drawing", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_service_outside_the_scope_does_not_exist()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        // A service whose every circuit hop is in the south: someone who sees the north only does not see it.
        var south = await Scalar(db, """
            SELECT sc.service_id FROM service_circuit sc
            WHERE NOT EXISTS (
                SELECT 1 FROM circuit_hop h JOIN port p ON p.terminal_id = h.terminal_id JOIN equipment e ON e.id = p.equipment_id
                JOIN site s ON s.id = e.site_id
                WHERE h.circuit_id = sc.circuit_id AND ST_Y(ST_PointOnSurface(s.geom)) >= 6400000)
            ORDER BY sc.service_id LIMIT 1
            """);
        using var north = NetworkFixture.Client(api, "cmdb-demo-region", ["cmdb-region-nord"]);
        var seen = await north.GetAsync($"/api/trace?service={south}", Ct);
        (await north.GetAsync($"/api/services/{south}/drawing", Ct)).StatusCode.ShouldBe(seen.StatusCode);
    }

    private static async Task<long> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

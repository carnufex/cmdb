using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Plans;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Features.Plans;

/// <summary>
/// A plan's impact (#252): the circuits it touches with whether their path holds before and after, and the services on
/// them, inside the caller's scope.
/// </summary>
public sealed class PlanImpactTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_disconnect_breaks_the_circuits_through_it_in_the_plan_but_not_in_production()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        // A physical circuit carrying services, two of whose hops in a row are joined by a connection.
        await using var cmd = db.CreateCommand("""
            SELECT c.id, h1.terminal_id, h2.terminal_id
            FROM circuit c
            JOIN circuit_hop h1 ON h1.circuit_id = c.id JOIN circuit_hop h2 ON h2.circuit_id = c.id AND h2.seq = h1.seq + 1
            JOIN connection x ON x.valid_to IS NULL AND x.a_terminal_id = least(h1.terminal_id, h2.terminal_id) AND x.b_terminal_id = greatest(h1.terminal_id, h2.terminal_id)
            WHERE c.layer = 'physical' AND EXISTS (SELECT 1 FROM circuit_dependency d JOIN service_circuit s ON s.circuit_id = d.circuit_id WHERE d.carrier_id = c.id)
            ORDER BY c.id LIMIT 1
            """);
        long circuit, a, b;
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            (circuit, a, b) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
        var plan = (await (await client.PostAsJsonAsync("/api/plans", new { name = "Påverkan" }, Ct)).Content.ReadFromJsonAsync<PlanSummary>(Ct))!;
        try
        {
            (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/operations", new { kind = "disconnect", a, b }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            var url = $"/api/plans/{plan.Id}/impact";
            PlanImpact impact = null!;
            var fastest = double.MaxValue;
            // The budget is for warm calls (docs/plan.md, #280): the fastest of three after the first.
            for (var i = 0; i < 4; i++)
            {
                impact = (await client.GetFromJsonAsync<PlanImpact>(url, Ct))!;
                fastest = i > 0 ? Math.Min(fastest, impact.ElapsedMs) : fastest;
            }
            var hit = impact.Circuits.Where(c => c.Circuit.Id == circuit).ShouldHaveSingleItem();
            (hit.Before, hit.After).ShouldBe(("intact", "broken"));
            hit.Services.ShouldBeGreaterThan(0);
            impact.Broken.ShouldBeGreaterThanOrEqualTo(1);
            // Broken circuits first, and the services on them lose or weaken their path.
            impact.Circuits[0].After.ShouldBe("broken");
            impact.Services.ShouldContain(s => s.Before == "intact" && s.After != "intact");
            // Every other circuit the plan touches holds in production: the path check agrees with the model.
            impact.Circuits.ShouldAllBe(c => c.Before == "intact");
            fastest.ShouldBeLessThan(200);

            (await client.GetAsync("/api/plans/999999999/impact", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await using var clean = db.CreateCommand($"DELETE FROM plan_operation WHERE plan_id = {plan.Id}; DELETE FROM plan WHERE id = {plan.Id}");
            await clean.ExecuteNonQueryAsync(Ct);
        }
    }
}

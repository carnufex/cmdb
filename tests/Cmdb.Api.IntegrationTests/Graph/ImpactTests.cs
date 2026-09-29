using System.Net.Http.Json;
using Cmdb.Api.Features.Objects;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Graph;

/// <summary>Impact analysis in the graph (#10) against the recursive query it replaced, on the small generated network.</summary>
public sealed class ImpactTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The pre-#10 implementation: circuits through the object's terminals, recursively upwards, and their services.
    private const string Reference = """
        WITH RECURSIVE terminals AS ({0}),
        direct AS (
            SELECT DISTINCT h.circuit_id FROM circuit_hop h JOIN terminals t ON t.terminal_id = h.terminal_id
        ), affected AS (
            SELECT circuit_id FROM direct
            UNION
            SELECT d.circuit_id FROM circuit_dependency d JOIN affected a ON d.carrier_id = a.circuit_id
        )
        SELECT (SELECT count(*) FROM affected)::int, (SELECT count(*) FROM direct)::int,
               coalesce((SELECT array_agg(DISTINCT sc.service_id ORDER BY sc.service_id) FROM service_circuit sc JOIN affected a ON a.circuit_id = sc.circuit_id), '{}')
        """;

    private static readonly Dictionary<string, string> Terminals = new()
    {
        ["cable"] = "SELECT ce.terminal_id FROM conductor co JOIN conductor_end ce ON ce.conductor_id = co.id WHERE co.cable_id = $1",
        ["equipment"] = "SELECT p.terminal_id FROM port p WHERE p.equipment_id = $1",
        ["site"] = "SELECT p.terminal_id FROM equipment e JOIN port p ON p.equipment_id = e.id WHERE e.site_id = $1",
    };

    [Theory]
    [InlineData("cable", "cables", "SELECT id FROM cable ORDER BY md5(id::text) LIMIT 40")]
    [InlineData("equipment", "equipment", "SELECT id FROM equipment ORDER BY md5(id::text) LIMIT 40")]
    [InlineData("site", "sites", "SELECT id FROM site WHERE site_type IN ('hub', 'aggregation') UNION ALL (SELECT id FROM site ORDER BY md5(id::text) LIMIT 20)")]
    public async Task Matches_the_recursive_query_it_replaced(string type, string path, string sample)
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        var ids = await Ids(db, sample);
        ids.Count.ShouldBeGreaterThan(10);

        var nonEmpty = 0;
        foreach (var id in ids)
        {
            var impact = (await client.GetFromJsonAsync<Impact>($"/api/{path}/{id}/impact", Ct))!;
            var (circuits, direct, services) = await Expected(db, type, id);

            impact.Circuits.ShouldBe(circuits, $"{type} {id}");
            impact.Direct.ShouldBe(direct, $"{type} {id}");
            impact.Services.Select(s => s.Service.Id).Order().ToArray().ShouldBe(services, $"{type} {id}");
            foreach (var s in impact.Services)
            {
                s.Path.ShouldNotBeEmpty();
                s.Path.Select(c => c.Layer).ShouldBe(s.Path.Select(c => c.Layer).OrderBy(Rank), $"layers go downwards for {s.Service.Code}");
            }
            nonEmpty += impact.Services.Count > 0 ? 1 : 0;
        }
        nonEmpty.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_radio_cable_cut_names_the_backhaul_and_the_fibre_it_rides_on()
    {
        var (db, api) = await NetworkFixture.GetAsync(factory);
        using var client = NetworkFixture.Client(api);
        var id = (await Ids(db, """
            SELECT c.id FROM cable c JOIN site s ON s.id = c.a_site_id
            WHERE s.site_type = 'radio' AND c.lifecycle = 'in_service' ORDER BY c.id LIMIT 1
            """))[0];

        var impact = (await client.GetFromJsonAsync<Impact>($"/api/cables/{id}/impact", Ct))!;

        var backhaul = impact.Services.First(s => s.Service.Name!.StartsWith("Mobil backhaul", StringComparison.Ordinal));
        backhaul.Path[0].Layer.ShouldBe("logical");
        backhaul.Path[^1].Layer.ShouldBe("physical");
        impact.ElapsedMs.ShouldBeLessThan(200);
    }

    [Fact]
    public async Task Unknown_objects_affect_nothing()
    {
        using var client = factory.CreateAuthenticatedClient();

        var impact = (await client.GetFromJsonAsync<Impact>("/api/equipment/999999999/impact", Ct))!;

        impact.Circuits.ShouldBe(0);
        impact.Services.ShouldBeEmpty();
    }

    private static int Rank(string layer) => layer switch { "logical" => 0, "transmission" => 1, _ => 2 };

    private static async Task<List<long>> Ids(NpgsqlDataSource db, string sql)
    {
        var ids = new List<long>();
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static async Task<(int Circuits, int Direct, long[] Services)> Expected(NpgsqlDataSource db, string type, long id)
    {
        await using var cmd = db.CreateCommand(Reference.Replace("{0}", Terminals[type], StringComparison.Ordinal));
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetFieldValue<long[]>(2));
    }
}

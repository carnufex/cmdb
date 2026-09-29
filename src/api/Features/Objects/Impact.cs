using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Objects;

public sealed record ImpactRequest(long Id);

/// <summary>What would be affected: circuits through the object and, following dependencies upwards, services.</summary>
public sealed record Impact(int Circuits, IReadOnlyList<ObjectRef> Services);

/// <summary>
/// Impact of a cable or a site: every circuit whose path touches one of its terminals, every circuit riding on
/// those (recursively) and the services they carry. Budget: 200 ms for a cable span (docs/plan.md). The graph
/// engine (#8, #10) takes this over; until then it is a recursive query.
/// </summary>
public sealed class ImpactEndpoint(NpgsqlDataSource db) : Endpoint<ImpactRequest, Impact>
{
    // TODO(#22): only services within the caller's scope are listed; the rest are counted.
    private const string Sql = """
        WITH RECURSIVE terminals AS ({0}),
        direct AS (
            SELECT DISTINCT h.circuit_id FROM circuit_hop h JOIN terminals t ON t.terminal_id = h.terminal_id
        ), affected AS (
            SELECT circuit_id FROM direct
            UNION
            SELECT d.circuit_id FROM circuit_dependency d JOIN affected a ON d.carrier_id = a.circuit_id
        )
        SELECT (SELECT count(*) FROM affected)::int, v.id, v.code, v.name, v.lifecycle::text
        FROM (SELECT DISTINCT sc.service_id FROM service_circuit sc JOIN affected a ON a.circuit_id = sc.circuit_id) s
        JOIN service v ON v.id = s.service_id
        UNION ALL
        SELECT (SELECT count(*) FROM affected)::int, NULL, NULL, NULL, NULL
        ORDER BY 3 NULLS LAST
        """;

    private const string CableTerminals =
        "SELECT ce.terminal_id FROM conductor co JOIN conductor_end ce ON ce.conductor_id = co.id WHERE co.cable_id = $1";

    private const string SiteTerminals =
        "SELECT p.terminal_id FROM equipment e JOIN port p ON p.equipment_id = e.id WHERE e.site_id = $1";

    private static readonly string CableSql = Sql.Replace("{0}", CableTerminals, StringComparison.Ordinal);
    private static readonly string SiteSql = Sql.Replace("{0}", SiteTerminals, StringComparison.Ordinal);

    public override void Configure() => Get("/cables/{id}/impact", "/sites/{id}/impact");

    public override async Task HandleAsync(ImpactRequest req, CancellationToken ct)
    {
        var isSite = HttpContext.Request.Path.Value!.Contains("/sites/", StringComparison.Ordinal);
        await Send.OkAsync(await RunAsync(db, isSite ? "site" : "cable", req.Id, ct), ct);
    }

    /// <summary>Impact of a cable or a site. Also used by the MCP tool <c>impact</c> (#61).</summary>
    internal static async Task<Impact> RunAsync(NpgsqlDataSource db, string type, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(type == "site" ? SiteSql : CableSql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });

        var circuits = 0;
        var services = new List<ObjectRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            circuits = reader.GetInt32(0);
            if (!reader.IsDBNull(1))
            {
                services.Add(new ObjectRef("service", reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }
        return new Impact(circuits, services);
    }
}

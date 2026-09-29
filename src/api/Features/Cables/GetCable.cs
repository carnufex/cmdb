using System.Text.Json;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Auth;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Cables;

public sealed record CableRequest(long Id);

public sealed record CableDetail(
    long Id,
    string Code,
    string TypeName,
    string Medium,
    int Conductors,
    int ConductorsInUse,
    double LengthM,
    string Lifecycle,
    JsonElement Attributes,
    ObjectRef A,
    ObjectRef B,
    IReadOnlyList<ObjectRef> Circuits);

/// <summary>
/// A cable, its ends and the circuits routed directly through its conductors. What depends on it further up
/// the layers is impact analysis, served separately with its own budget (CableImpact).
/// </summary>
public sealed class GetCableEndpoint(RequestDb db) : Endpoint<CableRequest, CableDetail>
{
    public override void Configure() => Get("/cables/{id}");

    public override async Task HandleAsync(CableRequest req, CancellationToken ct)
    {
        var detail = await LoadAsync(db, req.Id, HttpContext.Scope(), ct);
        if (detail is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(detail, ct);
    }

    /// <summary>Also used by the MCP tools (#61), so agents see exactly what the UI shows.</summary>
    internal static async Task<CableDetail?> LoadAsync(NpgsqlDataSource db, long id, UserScope scope, CancellationToken ct)
    {
        // The cable is visible when its scope rule says so (#22); an end site outside the scope is a placeholder, and
        // only circuits in scope are listed.
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new($"""
                    SELECT c.id, c.code, ct.name, ct.medium::text, ct.conductor_count, c.length_m, c.lifecycle::text, c.attributes::text,
                           a.id, a.code, a.name, a.lifecycle::text, b.id, b.code, b.name, b.lifecycle::text,
                           {ScopeSql.Site("a.id", 2)}, {ScopeSql.Site("b.id", 2)}
                    FROM cable c
                    JOIN cable_type ct ON ct.id = c.cable_type_id
                    JOIN site a ON a.id = c.a_site_id
                    JOIN site b ON b.id = c.b_site_id
                    WHERE c.id = $1 AND {ScopeSql.Cable("c.id", 2)}
                    """) { Parameters = { new() { Value = id }, scope.Parameter() } },
                new($"""
                    SELECT DISTINCT r.id, r.code, r.layer::text, r.lifecycle::text
                    FROM conductor co
                    JOIN conductor_end ce ON ce.conductor_id = co.id
                    JOIN circuit_hop h ON h.terminal_id = ce.terminal_id
                    JOIN circuit r ON r.id = h.circuit_id
                    WHERE co.cable_id = $1 AND {ScopeSql.Circuit("r.id", 2)}
                    ORDER BY r.code
                    """) { Parameters = { new() { Value = id }, scope.Parameter() } },
                new("""
                    SELECT count(DISTINCT ce.conductor_id)::int
                    FROM conductor co JOIN conductor_end ce ON ce.conductor_id = co.id JOIN circuit_hop h ON h.terminal_id = ce.terminal_id
                    WHERE co.cable_id = $1
                    """) { Parameters = { new() { Value = id } } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var cable = new CableDetail(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), 0,
            reader.GetDouble(5), reader.GetString(6), Terminals.Json(scope.MaskAttributes(reader.GetString(7))),
            reader.GetBoolean(16)
                ? new ObjectRef("site", reader.GetInt64(8), reader.GetString(9), reader.GetString(10), reader.GetString(11))
                : ObjectRef.Hidden("site"),
            reader.GetBoolean(17)
                ? new ObjectRef("site", reader.GetInt64(12), reader.GetString(13), reader.GetString(14), reader.GetString(15))
                : ObjectRef.Hidden("site"),
            []);

        await reader.NextResultAsync(ct);
        var circuits = new List<ObjectRef>();
        while (await reader.ReadAsync(ct))
        {
            circuits.Add(new ObjectRef("circuit", reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        await reader.NextResultAsync(ct);
        await reader.ReadAsync(ct);
        return cable with
        {
            ConductorsInUse = reader.GetInt32(0),
            Circuits = circuits,
        };
    }
}

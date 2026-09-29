using Cmdb.Api.Features.Objects;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Circuits;

public sealed record CircuitRequest(long Id);

public sealed record CircuitHop(int Seq, TerminalRef Terminal, string? Channel);

public sealed record CircuitDetail(
    long Id,
    string Code,
    string Layer,
    string Lifecycle,
    IReadOnlyList<CircuitHop> Hops,
    IReadOnlyList<ObjectRef> Carriers,
    IReadOnlyList<ObjectRef> Carried,
    IReadOnlyList<ObjectRef> Services);

/// <summary>A circuit's path hop by hop, the circuits it rides on, the ones riding on it and its services.</summary>
public sealed class GetCircuitEndpoint(NpgsqlDataSource db) : Endpoint<CircuitRequest, CircuitDetail>
{
    public override void Configure() => Get("/circuits/{id}");

    public override async Task HandleAsync(CircuitRequest req, CancellationToken ct)
    {
        var detail = await LoadAsync(db, req.Id, ct);
        if (detail is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(detail, ct);
    }

    /// <summary>Also used by the MCP tools (#61), so agents see exactly what the UI shows.</summary>
    internal static async Task<CircuitDetail?> LoadAsync(NpgsqlDataSource db, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new("SELECT id, code, layer::text, lifecycle::text FROM circuit WHERE id = $1") { Parameters = { new() { Value = id } } },
                new("""
                    SELECT h.seq, h.terminal_id, ch.kind::text || ' ' || ch.number
                    FROM circuit_hop h LEFT JOIN channel ch ON ch.id = h.channel_id
                    WHERE h.circuit_id = $1 ORDER BY h.seq
                    """) { Parameters = { new() { Value = id } } },
                new("""
                    SELECT 'carrier', r.id, r.code, r.layer::text, r.lifecycle::text
                    FROM circuit_dependency d JOIN circuit r ON r.id = d.carrier_id WHERE d.circuit_id = $1
                    UNION ALL
                    SELECT 'carried', r.id, r.code, r.layer::text, r.lifecycle::text
                    FROM circuit_dependency d JOIN circuit r ON r.id = d.circuit_id WHERE d.carrier_id = $1
                    UNION ALL
                    SELECT 'service', v.id, v.code, v.name, v.lifecycle::text
                    FROM service_circuit sc JOIN service v ON v.id = sc.service_id WHERE sc.circuit_id = $1
                    """) { Parameters = { new() { Value = id } } },
            },
        };

        CircuitDetail circuit;
        var hops = new List<(int Seq, long Terminal, string? Channel)>();
        var related = new List<(string Kind, ObjectRef Ref)>();
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            circuit = new CircuitDetail(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), [], [], [], []);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                hops.Add((reader.GetInt32(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var kind = reader.GetString(0);
                related.Add((kind, new ObjectRef(kind == "service" ? "service" : "circuit", reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))));
            }
        }

        var terminals = await Terminals.DescribeAsync(conn, [.. hops.Select(h => h.Terminal).Distinct()], ct);
        return circuit with
        {
            Hops = [.. hops.Where(h => terminals.ContainsKey(h.Terminal)).Select(h => new CircuitHop(h.Seq, terminals[h.Terminal], h.Channel))],
            Carriers = [.. related.Where(r => r.Kind == "carrier").Select(r => r.Ref)],
            Carried = [.. related.Where(r => r.Kind == "carried").Select(r => r.Ref)],
            Services = [.. related.Where(r => r.Kind == "service").Select(r => r.Ref)],
        };
    }
}

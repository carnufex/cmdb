using System.Text.Json;
using Cmdb.Api.Features.Objects;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Services;

public sealed record ServiceRequest(long Id);

public sealed record ServiceCircuit(ObjectRef Circuit, TerminalRef? A, TerminalRef? B);

public sealed record ServiceDetail(
    long Id,
    string Code,
    string Name,
    string ServiceType,
    string Lifecycle,
    JsonElement Attributes,
    IReadOnlyList<ServiceCircuit> Circuits);

/// <summary>A service and the circuits carrying it, with where each one starts and ends.</summary>
public sealed class GetServiceEndpoint(NpgsqlDataSource db) : Endpoint<ServiceRequest, ServiceDetail>
{
    public override void Configure() => Get("/services/{id}");

    public override async Task HandleAsync(ServiceRequest req, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new("SELECT id, code, name, service_type, lifecycle::text, attributes::text FROM service WHERE id = $1") { Parameters = { new() { Value = req.Id } } },
                new("""
                    SELECT r.id, r.code, r.layer::text, r.lifecycle::text, r.a_terminal_id, r.b_terminal_id
                    FROM service_circuit sc JOIN circuit r ON r.id = sc.circuit_id WHERE sc.service_id = $1 ORDER BY r.code
                    """) { Parameters = { new() { Value = req.Id } } },
            },
        };

        ServiceDetail service;
        var circuits = new List<(ObjectRef Ref, long A, long B)>();
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            service = new ServiceDetail(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), Terminals.Json(reader.GetString(5)), []);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                circuits.Add((new ObjectRef("circuit", reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                    reader.GetInt64(4), reader.GetInt64(5)));
            }
        }

        var terminals = await Terminals.DescribeAsync(conn, [.. circuits.SelectMany(c => new[] { c.A, c.B }).Distinct()], ct);
        await Send.OkAsync(service with
        {
            Circuits = [.. circuits.Select(c => new ServiceCircuit(c.Ref, terminals.GetValueOrDefault(c.A), terminals.GetValueOrDefault(c.B)))],
        }, ct);
    }
}

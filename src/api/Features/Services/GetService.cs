using System.Text.Json;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Auth;
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
    IReadOnlyList<ServiceCircuit> Circuits,
    IReadOnlyList<ObjectSource>? Sources = null);

/// <summary>A service and the circuits carrying it, with where each one starts and ends.</summary>
public sealed class GetServiceEndpoint(RequestDb db) : Endpoint<ServiceRequest, ServiceDetail>
{
    public override void Configure() => Get("/services/{id}");

    public override async Task HandleAsync(ServiceRequest req, CancellationToken ct)
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
    internal static async Task<ServiceDetail?> LoadAsync(NpgsqlDataSource db, long id, UserScope scope, CancellationToken ct)
    {
        // A service is visible when a circuit carrying it is (#22); only those circuits are listed.
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new($"SELECT id, code, name, service_type, lifecycle::text, attributes::text FROM service WHERE id = $1 AND {ScopeSql.Service("service.id", 2)}")
                {
                    Parameters = { new() { Value = id }, scope.Parameter() },
                },
                new($"""
                    SELECT r.id, r.code, r.layer::text, r.lifecycle::text, r.a_terminal_id, r.b_terminal_id
                    FROM service_circuit sc JOIN circuit r ON r.id = sc.circuit_id
                    WHERE sc.service_id = $1 AND {ScopeSql.Circuit("r.id", 2)} ORDER BY r.code
                    """) { Parameters = { new() { Value = id }, scope.Parameter() } },
            },
        };

        ServiceDetail service;
        var circuits = new List<(ObjectRef Ref, long A, long B)>();
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            service = new ServiceDetail(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), Terminals.Json(scope.MaskAttributes(reader.GetString(5))), []);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                circuits.Add((new ObjectRef("circuit", reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                    reader.GetInt64(4), reader.GetInt64(5)));
            }
        }

        var terminals = await Terminals.DescribeAsync(conn, [.. circuits.SelectMany(c => new[] { c.A, c.B }).Distinct()], scope, ct);
        return service with
        {
            Circuits = [.. circuits.Select(c => new ServiceCircuit(c.Ref, terminals.GetValueOrDefault(c.A), terminals.GetValueOrDefault(c.B)))],
            Sources = await Sources.LoadAsync(conn, "service", id, scope, ct),
        };
    }
}

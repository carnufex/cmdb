using Cmdb.Api.Auth;
using FastEndpoints;

namespace Cmdb.Api.Features.Voice;

public sealed record ServiceRequestRow(string Number, string Kind, string Status, string? EmployeeId, string CallerName, string Summary,
    string ConversationId, DateTimeOffset CreatedAt);

public sealed record ServiceRequestList(int Waiting, int QueueMinutes, IReadOnlyList<ServiceRequestRow> Requests);

public sealed class ServiceRequestNumber
{
    public string Number { get; set; } = "";
}

/// <summary>Marks a service request done from the agent panel (#161); a done callback leaves the queue.</summary>
public sealed class CompleteServiceRequestEndpoint(SystemDb system) : Endpoint<ServiceRequestNumber>
{
    public override void Configure()
    {
        Post("/voice/service-requests/{number}/done");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(ServiceRequestNumber req, CancellationToken ct)
    {
        // Like the list: requests name people, so only callers who see the whole network handle them.
        if (!HttpContext.Scope().Unrestricted || !req.Number.StartsWith("SR-", StringComparison.Ordinal)
            || !long.TryParse(req.Number.AsSpan(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await using var cmd = system.Source.CreateCommand("UPDATE service_request SET status = 'done' WHERE id = $1");
        cmd.Parameters.Add(new() { Value = id });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.NoContentAsync(ct);
    }
}

/// <summary>
/// Requests to the service desk and IT self-service agents (ADR-0016, #151) and the callback queue, for the agent panel.
/// They name people, and are not network data under the access scopes, so only callers with the whole network see them,
/// as with the SMS outbox.
/// </summary>
public sealed class ListServiceRequestsEndpoint(SystemDb system) : EndpointWithoutRequest<ServiceRequestList>
{
    public override void Configure() => Get("/voice/service-requests");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!HttpContext.Scope().Unrestricted)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var rows = new List<ServiceRequestRow>();
        var waiting = 0;
        await using (var cmd = system.Source.CreateCommand("""
            SELECT id, kind, status, employee_id, caller_name, summary, conversation_id, created_at,
                   count(*) FILTER (WHERE kind = 'callback' AND status = 'open') OVER ()
            FROM service_request ORDER BY id DESC LIMIT 50
            """))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ServiceRequestRow(ServiceDeskTools.Number(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetFieldValue<DateTimeOffset>(7)));
                waiting = (int)reader.GetInt64(8);
            }
        }
        await Send.OkAsync(new ServiceRequestList(waiting, ServiceDeskTools.Minutes(waiting), rows), ct);
    }
}

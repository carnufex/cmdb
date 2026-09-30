using Cmdb.Api.Auth;
using FastEndpoints;

namespace Cmdb.Api.Features.Voice;

public sealed record ServiceRequestRow(string Number, string Kind, string Status, string? EmployeeId, string CallerName, string Summary,
    string ConversationId, DateTimeOffset CreatedAt);

public sealed record ServiceRequestList(int Waiting, int QueueMinutes, IReadOnlyList<ServiceRequestRow> Requests);

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

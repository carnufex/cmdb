using System.Globalization;
using System.Text.Json;
using Cmdb.Api.Auth;
using FastEndpoints;

namespace Cmdb.Api.Features.Voice;

public sealed record IncidentRow(string Number, string Priority, string Status, long SiteId, string SiteCode, string SiteName, string Reference,
    string Description, string Observations, string ReportedBy, string ConversationId, DateTimeOffset CreatedAt, JsonElement Enrichment);

public sealed record SmsRow(string ToPhone, string EmployeeId, string Body, DateTimeOffset CreatedAt);

public sealed record ToolCallRow(string ConversationId, string? EmployeeId, string Tool, string Outcome, int Milliseconds, DateTimeOffset CreatedAt);

public sealed record VoiceActivity(IReadOnlyList<SmsRow> Sms, IReadOnlyList<ToolCallRow> Calls);

public static class Incidents
{
    public static string Number(long id) => string.Create(CultureInfo.InvariantCulture, $"INC-{id:D5}");
}

/// <summary>Incidents from the operations agent (#135), newest first, at sites within the caller's access scopes.</summary>
public sealed class ListIncidentsEndpoint(RequestDb db) : EndpointWithoutRequest<IReadOnlyList<IncidentRow>>
{
    public override void Configure() => Get("/incidents");

    public override async Task HandleAsync(CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand($"""
            SELECT i.id, i.priority, i.status, i.site_id, s.code, s.name, i.reference, i.description, i.observations, i.reported_by,
                   i.conversation_id, i.created_at, i.enrichment
            FROM incident i JOIN site s ON s.id = i.site_id
            WHERE {ScopeSql.Site("i.site_id", 1)}
            ORDER BY i.id DESC LIMIT 50
            """);
        cmd.Parameters.Add(HttpContext.Scope().Parameter());
        var rows = new List<IncidentRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new IncidentRow(Incidents.Number(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11), JsonDocument.Parse(reader.GetString(12)).RootElement.Clone()));
        }
        await Send.OkAsync(rows, ct);
    }
}

/// <summary>
/// The stubbed SMS outbox and the voice channel's tool calls (ADR-0015), for the demo's agent panel. Codes are
/// secrets, so only callers whose scopes cover the whole network see them.
/// </summary>
public sealed class VoiceActivityEndpoint(SystemDb system) : EndpointWithoutRequest<VoiceActivity>
{
    public override void Configure() => Get("/voice/activity");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!HttpContext.Scope().Unrestricted)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var sms = new List<SmsRow>();
        await using (var cmd = system.Source.CreateCommand("SELECT to_phone, employee_id, body, created_at FROM voice_sms ORDER BY id DESC LIMIT 10"))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sms.Add(new SmsRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }
        var calls = new List<ToolCallRow>();
        await using (var cmd = system.Source.CreateCommand(
            "SELECT conversation_id, employee_id, tool, outcome, milliseconds, created_at FROM voice_tool_call ORDER BY id DESC LIMIT 30"))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                calls.Add(new ToolCallRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }
        await Send.OkAsync(new VoiceActivity(sms, calls), ct);
    }
}

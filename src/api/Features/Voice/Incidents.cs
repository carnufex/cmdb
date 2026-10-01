using System.Globalization;
using System.Text.Json;
using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Voice;

public sealed record IncidentRow(string Number, string Priority, string Status, long SiteId, string SiteCode, string SiteName, string Reference,
    string Description, string Observations, string ReportedBy, string ConversationId, DateTimeOffset CreatedAt, JsonElement Enrichment,
    DateTimeOffset? ResolvedAt = null, string? ResolvedBy = null);

public sealed record SmsRow(string ToPhone, string EmployeeId, string Body, DateTimeOffset CreatedAt);

public sealed record ToolCallRow(string ConversationId, string? EmployeeId, string Tool, string Outcome, int Milliseconds, DateTimeOffset CreatedAt);

public sealed record VoiceActivity(IReadOnlyList<SmsRow> Sms, IReadOnlyList<ToolCallRow> Calls);

public static class Incidents
{
    public static string Number(long id) => string.Create(CultureInfo.InvariantCulture, $"INC-{id:D5}");

    /// <summary>The id behind "INC-00012", or null for anything else.</summary>
    public static long? Id(string number) =>
        number.StartsWith("INC-", StringComparison.Ordinal) && long.TryParse(number.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}

public sealed class IncidentNumberRequest
{
    public string Number { get; set; } = "";
}

/// <summary>
/// Resolves an incident from the agent panel (#161): closed, with when and by whom. People with write access only, and
/// only within their scopes; resolving one that is already closed changes nothing.
/// </summary>
public sealed class ResolveIncidentEndpoint(RequestDb db) : Endpoint<IncidentNumberRequest>
{
    public override void Configure()
    {
        Post("/incidents/{number}/resolve");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(IncidentNumberRequest req, CancellationToken ct)
    {
        if (Incidents.Id(req.Number) is not { } id)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await using var cmd = db.Source.CreateCommand($"""
            WITH target AS (SELECT i.id, i.status FROM incident i WHERE i.id = $2 AND {ScopeSql.Site("i.site_id", 1)}),
                 resolved AS (UPDATE incident SET status = 'closed', resolved_at = now(), resolved_by = $3
                              WHERE id IN (SELECT id FROM target WHERE status = 'open') RETURNING id)
            SELECT count(*) FROM target
            """);
        cmd.Parameters.Add(HttpContext.Scope().Parameter());
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = User.FindFirstValue(CmdbClaims.Username) ?? User.Identity?.Name ?? "?" });
        if ((long)(await cmd.ExecuteScalarAsync(ct))! == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.NoContentAsync(ct);
    }
}

/// <summary>
/// What an incident's fault takes down, as routes for the map (#161): the same as the live call's (#156), so overlapping
/// incidents can be shown one at a time. Within the caller's scopes.
/// </summary>
public sealed class IncidentImpactEndpoint(GraphHolder holder, RequestDb db, ScopeMasks masks) : Endpoint<IncidentNumberRequest, LiveImpact>
{
    public override void Configure() => Get("/incidents/{number}/impact");

    public override async Task HandleAsync(IncidentNumberRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        string? reference = null;
        if (Incidents.Id(req.Number) is { } id)
        {
            await using var cmd = db.Source.CreateCommand($"SELECT i.reference FROM incident i WHERE i.id = $2 AND {ScopeSql.Site("i.site_id", 1)}");
            cmd.Parameters.Add(scope.Parameter());
            cmd.Parameters.Add(new() { Value = id });
            reference = (string?)await cmd.ExecuteScalarAsync(ct);
        }
        if (reference is null || holder.Current is not { } graph || OperationsImpact.Parse(reference) is not { } target)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var mask = await masks.GetAsync(graph, scope, ct);
        if (await FaultAnalysis.RunAsync(graph, mask, db.Source, target.Type, target.Id, ct) is not { } fault)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(await OperationsImpact.RoutesAsync(graph, mask, db.Source, fault, scope, ct), ct);
    }
}

/// <summary>Incidents from the operations agent (#135), newest first, at sites within the caller's access scopes.</summary>
public sealed class ListIncidentsEndpoint(RequestDb db) : EndpointWithoutRequest<IReadOnlyList<IncidentRow>>
{
    public override void Configure() => Get("/incidents");

    public override async Task HandleAsync(CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand($"""
            SELECT i.id, i.priority, i.status, i.site_id, s.code, s.name, i.reference, i.description, i.observations, i.reported_by,
                   i.conversation_id, i.created_at, i.enrichment, i.resolved_at, i.resolved_by
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
                reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11), JsonDocument.Parse(reader.GetString(12)).RootElement.Clone(),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
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

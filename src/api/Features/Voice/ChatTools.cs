using System.ComponentModel;
using Cmdb.Api.Agents;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Operations;
using Cmdb.Api.Features.Reservations;
using Cmdb.Graph;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cmdb.Api.Features.Voice;

public sealed record AgentIncident(string Number, string Priority, string Status, string Site, string SiteCode, string SiteName, string Description, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, string Url);

public sealed record AgentRisk(string Id, string Kind, string Title, string Description, string Reference, string SiteCode, string SiteName, string Url);

public sealed record AgentRisks(int Total, IReadOnlyList<AgentRisk> Risks);

public sealed record AgentHistoryEntry(DateTimeOffset At, string Kind, string? Actor, string? Why, IReadOnlyList<FieldChange> Changes);

public sealed record AgentHistory(string Ref, string Url, int Total, IReadOnlyList<AgentHistoryEntry> Entries);

/// <summary>What the operations view shows, for an agent asking as a person (#334): always within the person's scopes, like the endpoints.</summary>
[McpServerToolType]
public sealed class ChatTools(RequestDb db, GraphHolder holder, ScopeMasks masks, AgentLinks links, Classifications.ClassificationRules rules, IHttpContextAccessor http)
{
    private const int MaxEntries = 50;

    private UserScope Scope => http.HttpContext!.Scope();

    [McpServerTool(Name = "list_incidents", Title = "Ärenden", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Incidents at sites the person can see, newest first (at most 50): faults reported, their priority and whether they are resolved. Use it for \"what is broken right now\".")]
    public async Task<IReadOnlyList<AgentIncident>> ListIncidents(
        [Description("Only those not yet resolved.")] bool openOnly = true,
        [Description("Only this site, by code, e.g. \"AGG-000315\".")] string? siteCode = null,
        CancellationToken ct = default)
    {
        var rows = await ListIncidentsEndpoint.LoadAsync(db.Source, Scope, ct);
        return [.. rows
            .Where(r => !openOnly || r.ResolvedAt is null)
            .Where(r => siteCode is null || string.Equals(r.SiteCode, siteCode.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(r => new AgentIncident(r.Number, r.Priority, r.Status, AgentLinks.Ref("site", r.SiteId), r.SiteCode, r.SiteName, r.Description, r.CreatedAt, r.ResolvedAt, links.For("site", r.SiteId)))];
    }

    [McpServerTool(Name = "list_risks", Title = "Risker", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Risks in what the person can see: planned digging across a cable, false redundancy and old backup batteries. risk_details gives one in full.")]
    public async Task<AgentRisks> ListRisks(
        [Description("Maximum listed, 1–50.")] int limit = 25,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxEntries)
        {
            throw new McpException($"limit must be between 1 and {MaxEntries}.");
        }
        if (holder.Current is not { } graph)
        {
            throw new McpException("The network graph is loading, try again in a few seconds.");
        }
        var risks = await RiskDetection.RunAsync(graph, await masks.GetAsync(graph, Scope, ct), db.Source, Scope, ct, rules);
        return new AgentRisks(risks.Count, [.. risks.Take(limit).Select(r => new AgentRisk(r.Id, r.Kind, r.Title, r.Description, r.Reference, r.SiteCode, r.SiteName, links.For("site", r.SiteId)))]);
    }

    [McpServerTool(Name = "get_history", Title = "Historik", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What changed on an object, newest first: when, who, why and which fields. Takes site, equipment, cable, circuit or service.")]
    public async Task<AgentHistory> GetHistory(
        [Description("The object, e.g. \"site:12\".")] string reference,
        [Description("Maximum entries, 1–50.")] int limit = 20,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxEntries)
        {
            throw new McpException($"limit must be between 1 and {MaxEntries}.");
        }
        var (type, id) = Parse(reference);
        var history = await HistoryEndpoint.LoadAsync(db.Source, type, id, Scope, ct)
            ?? throw new McpException($"No {type} with id {id} that you can see.");
        return new AgentHistory(AgentLinks.Ref(type, id), links.For(type, id), history.Entries.Count,
            [.. history.Entries.Take(limit).Select(e => new AgentHistoryEntry(e.At, e.Kind, e.Operation?.Actor, e.Operation?.Why, e.Changes))]);
    }

    [McpServerTool(Name = "list_reservations", Title = "Reservationer", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What a plan or a service holds reserved: conductors, slots and channels. Give one of plan or service.")]
    public async Task<IReadOnlyList<ReservationView>> ListReservations(
        [Description("The plan, \"plan:12\".")] string? plan = null,
        [Description("The service, \"service:12\".")] string? service = null,
        CancellationToken ct = default)
    {
        if ((plan is null) == (service is null))
        {
            throw new McpException("Give exactly one of plan or service.");
        }
        var (kind, id) = Parse(plan ?? service!);
        if (kind != (plan is null ? "service" : "plan") || !await ReservationSql.HolderVisibleAsync(db.Source, kind, id, Scope, draftOnly: false, ct))
        {
            throw new McpException($"No {kind} with id {id} that you can see.");
        }
        return await ReservationSql.ListAsync(db.Source, kind, id, null, ct);
    }

    private static (string Type, long Id) Parse(string reference)
    {
        var parts = reference.Trim().Split(':', 2);
        return parts.Length == 2 && long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? (parts[0], id)
            : throw new McpException($"A reference looks like \"site:12\", not \"{reference}\".");
    }
}

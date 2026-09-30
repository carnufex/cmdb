using System.ComponentModel;
using System.Globalization;
using Cmdb.Api.Agents;
using Cmdb.Api.Auth;
using Cmdb.Graph;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

public sealed record AgentPlan(string Ref, string Name, string Status, string? Flag, int Operations, int Conflicts, string CreatedBy,
    string CreatedVia, IReadOnlyList<string> DependsOn, string Url);

public sealed record AgentPlanChange(string Ref, string Plan, string Kind, string Summary, string? Problem, IReadOnlyList<string> Conflicts,
    bool Blocked);

/// <summary>A plan's view as an agent sees it: every change against production, what does not fit, and the link for a person.</summary>
public sealed record AgentPlanPreview(AgentPlan Plan, IReadOnlyList<AgentPlanChange> Changes, int Problems, int Conflicts,
    bool ReadyToApply, string Note);

public sealed record AgentPlanAdded(AgentPlan Plan, IReadOnlyList<AgentPlanChange> Added);

/// <summary>One change for <c>add_to_plan</c>.</summary>
public sealed class AgentPlanOperation
{
    [Description("connect, disconnect, set_lifecycle or rename.")]
    public string Kind { get; set; } = "";

    [Description("connect/disconnect: the first terminal id (ports and conductor ends have terminal ids in get_object).")]
    public long? A { get; set; }

    [Description("connect/disconnect: the second terminal id.")]
    public long? B { get; set; }

    [Description("connect: patch, splice, termination or internal. Default patch.")]
    public string? ConnectionKind { get; set; }

    [Description("set_lifecycle/rename: a reference \"site:12\", \"equipment:34\" or \"cable:56\" (rename: site or equipment).")]
    public string? Target { get; set; }

    [Description("set_lifecycle: planned, under_construction, in_service, decommissioning or removed.")]
    public string? Lifecycle { get; set; }

    [Description("rename: the new name.")]
    public string? Name { get; set; }
}

/// <summary>
/// Writing through plans (#64, ADR-0005, ADR-0011): agents propose changes as plans that a person reviews and brings
/// into production in the web app. There is no tool that writes to production, and no tool that applies a plan.
/// </summary>
[McpServerToolType]
public sealed class PlanTools(RequestDb db, PlanWrites writes, PlanViews views, GraphHolder holder, ScopeMasks masks, AgentLinks links,
    IHttpContextAccessor http)
{
    private const int MaxOperations = 100;

    private HttpContext Http => http.HttpContext!;

    [McpServerTool(Name = "list_plans", Title = "Planer", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Plans you can see: drafts first, with status, how many changes they hold, conflicts with other plans' claims, " +
        "and who made them. A plan's view is production plus the plans it depends on plus the plan itself.")]
    public async Task<IReadOnlyList<AgentPlan>> ListPlans(CancellationToken ct = default)
    {
        var scope = Http.Scope();
        var plans = await PlanSql.SummariesAsync(db, null, ct);
        return [.. plans.Where(p => scope.SeesPlan(p.Id))
            .OrderBy(p => p.Status == "draft" ? 0 : 1).ThenByDescending(p => p.UpdatedAt)
            .Select(ToAgent)];
    }

    [McpServerTool(Name = "create_plan", Title = "Skapa plan", Destructive = false, OpenWorld = false)]
    [Description("Creates a draft plan for proposed changes. Nothing changes in production: a person reviews the plan in the web " +
        "app and brings it in. Build on other plans with dependsOn when the changes need theirs first.")]
    public async Task<AgentPlan> CreatePlan(
        [Description("A short name a person recognises, e.g. \"Patcha SW-1 mot ODF-3\".")] string name,
        [Description("Why: what the change is for and anything the reviewer should know.")] string? description = null,
        [Description("Plans this one builds on, as references \"plan:12\".")] string[]? dependsOn = null,
        CancellationToken ct = default)
    {
        RequireWriter();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new McpException("name is 1–200 characters.");
        }
        var result = await writes.CreateAsync(Http.User, "mcp", Http.Scope(), name.Trim(), description?.Trim(),
            [.. (dependsOn ?? []).Select(r => ParseRef(r, "plan"))], ct);
        return result.Value is { } plan ? ToAgent(plan) : throw new McpException(result.Error!);
    }

    [McpServerTool(Name = "add_to_plan", Title = "Lägg till i plan", Destructive = false, OpenWorld = false)]
    [Description("Adds changes to a draft plan, in order. Each change is checked against the plan's view: it answers with what " +
        "does not fit production (already connected, occupied, not connected) and conflicts with other plans' claims or " +
        "reservations. Use connect_ports for a range of ports.")]
    public async Task<AgentPlanAdded> AddToPlan(
        [Description("The plan, \"plan:12\".")] string plan,
        [Description("The changes, at most 100.")] AgentPlanOperation[] operations,
        CancellationToken ct = default)
    {
        RequireWriter();
        var planId = ParseRef(plan, "plan");
        if (operations.Length is 0 or > MaxOperations)
        {
            throw new McpException($"Give 1–{MaxOperations} operations.");
        }
        var requests = new List<AddOperationRequest>();
        foreach (var op in operations)
        {
            var (type, id) = op.Target is null ? ((string?)null, (long?)null) : ParseObject(op.Target);
            requests.Add(new AddOperationRequest
            {
                Id = planId,
                Kind = op.Kind,
                A = op.A,
                B = op.B,
                ConnectionKind = op.Kind == "connect" ? op.ConnectionKind ?? "patch" : op.ConnectionKind,
                Type = type,
                ObjectId = id,
                Lifecycle = op.Lifecycle,
                Name = op.Name,
            });
        }
        return await AddAllAsync(planId, requests, ct);
    }

    [McpServerTool(Name = "connect_ports", Title = "Koppla portintervall", Destructive = false, OpenWorld = false)]
    [Description("Adds connections for a range of ports to a draft plan, e.g. \"patch port 1–24 on SW-1 to ODF-3\": the n-th port " +
        "from the first start port connects to the n-th port from the second, in front-panel order. Ports are named as in " +
        "get_object (e.g. \"ge-0/0/1\") or given by position (\"1\").")]
    public async Task<AgentPlanAdded> ConnectPorts(
        [Description("The plan, \"plan:12\".")] string plan,
        [Description("First equipment, \"equipment:34\" or its exact name.")] string from,
        [Description("First port on it: name or position.")] string fromPort,
        [Description("Second equipment, \"equipment:56\" or its exact name.")] string to,
        [Description("First port on it: name or position.")] string toPort,
        [Description("How many ports, 1–96.")] int count,
        [Description("patch, splice, termination or internal. Default patch.")] string connectionKind = "patch",
        CancellationToken ct = default)
    {
        RequireWriter();
        var planId = ParseRef(plan, "plan");
        if (count is < 1 or > 96)
        {
            throw new McpException("count is 1–96.");
        }
        var a = await PortRunAsync(from, fromPort, count, ct);
        var b = await PortRunAsync(to, toPort, count, ct);
        return await AddAllAsync(planId, [.. a.Zip(b, (x, y) => new AddOperationRequest
        {
            Id = planId,
            Kind = "connect",
            A = x,
            B = y,
            ConnectionKind = connectionKind,
        })], ct);
    }

    [McpServerTool(Name = "preview_plan", Title = "Förhandsvisa plan", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The plan's changes against production, including those of the plans it builds on, with what does not fit and " +
        "conflicts. Give the url to the person who will review and apply it; agents cannot apply plans.")]
    public async Task<AgentPlanPreview> PreviewPlan([Description("The plan, \"plan:12\".")] string plan, CancellationToken ct = default)
    {
        var planId = ParseRef(plan, "plan");
        var scope = Http.Scope();
        var graph = holder.Require();
        var view = await views.GetAsync(graph, planId, scope, ct) ?? throw new McpException($"No plan with id {planId}.");
        var mask = await masks.GetAsync(graph, scope, ct);
        var changes = await PlanSql.DescribeAsync(db, graph, mask, scope, view.Chain.Operations, view.Problems, ct);
        var names = view.Chain.Plans.ToDictionary(p => p.Id, p => p.Name);
        var summary = (await PlanSql.SummariesAsync(db, [planId], ct)).Single();
        var problems = changes.Count(c => c.Problem is not null);
        var conflicts = changes.Count(c => c.Conflicts.Count > 0);
        var ready = summary.Status == "draft" && problems == 0 && !changes.Any(c => c.Blocked)
            && view.Chain.Plans.All(p => p.Id == planId);
        return new AgentPlanPreview(ToAgent(summary),
            [.. changes.Select(c => ToAgent(c, names))],
            problems,
            conflicts,
            ready,
            ready ? "Ready for a person to review and apply in the web app (url)."
                : summary.Status != "draft" ? $"The plan is {summary.Status}."
                : "Not ready: fix the problems, resolve blocking reservations, or wait for the plans it builds on.");
    }

    private async Task<AgentPlanAdded> AddAllAsync(long planId, List<AddOperationRequest> requests, CancellationToken ct)
    {
        var scope = Http.Scope();
        var added = new List<PlanOperationView>();
        foreach (var req in requests)
        {
            var result = await writes.AddAsync(Http.User, scope, req, ct);
            if (result.Value is null)
            {
                var done = added.Count == 0 ? "" : $" The {added.Count} change(s) before it were added.";
                throw new McpException($"Change {added.Count + 1}: {result.Error}{done}");
            }
            added.Add(result.Value);
        }
        var summary = (await PlanSql.SummariesAsync(db, [planId], ct)).Single();
        var names = new Dictionary<long, string> { [planId] = summary.Name };
        return new AgentPlanAdded(ToAgent(summary), [.. added.Select(a => ToAgent(a, names))]);
    }

    /// <summary>Terminal ids of <paramref name="count"/> ports from the start port, in front-panel order.</summary>
    private async Task<List<long>> PortRunAsync(string equipment, string start, int count, CancellationToken ct)
    {
        var scope = Http.Scope();
        long id;
        if (equipment.StartsWith("equipment:", StringComparison.Ordinal))
        {
            id = ParseRef(equipment, "equipment");
        }
        else
        {
            await using var find = db.CreateCommand($"SELECT e.id FROM equipment e WHERE e.name = $1 AND {ScopeSql.Site("e.site_id", 2)} LIMIT 2");
            find.Parameters.Add(new() { Value = equipment });
            find.Parameters.Add(scope.Parameter());
            var ids = new List<long>();
            await using (var reader = await find.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    ids.Add(reader.GetInt64(0));
                }
            }
            id = ids.Count == 1 ? ids[0] : throw new McpException(ids.Count == 0
                ? $"No equipment named \"{equipment}\". Use search."
                : $"Several equipment are named \"{equipment}\"; use its reference \"equipment:ID\".");
        }
        await using var cmd = db.CreateCommand($"""
            SELECT p.terminal_id, p.name, p.position FROM port p JOIN equipment e ON e.id = p.equipment_id
            WHERE e.id = $1 AND {ScopeSql.Site("e.site_id", 2)} ORDER BY p.position
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(scope.Parameter());
        var ports = new List<(long Terminal, string Name, int Position)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                ports.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }
        if (ports.Count == 0)
        {
            throw new McpException($"No equipment with id {id}.");
        }
        var first = ports.FindIndex(p => p.Name == start
            || (int.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && p.Position == n));
        if (first < 0)
        {
            throw new McpException($"No port \"{start}\" on equipment:{id}. Ports are {ports[0].Name} … {ports[^1].Name}.");
        }
        if (first + count > ports.Count)
        {
            throw new McpException($"equipment:{id} has only {ports.Count - first} ports from \"{start}\".");
        }
        return [.. ports.Skip(first).Take(count).Select(p => p.Terminal)];
    }

    /// <summary>Only people and agents allowed to propose changes may write plans.</summary>
    private void RequireWriter()
    {
        if (!Http.User.IsInRole("cmdb-full") && !Http.User.IsInRole("cmdb-agents"))
        {
            throw new McpException("You may read but not propose changes.");
        }
    }

    private AgentPlan ToAgent(PlanSummary p) => new(AgentLinks.Ref("plan", p.Id), p.Name, p.Status, p.Flag, p.Operations, p.Conflicts,
        p.CreatedBy, p.CreatedVia, [.. p.DependsOn.Select(d => AgentLinks.Ref("plan", d))], links.ForPlan(p.Id));

    private static AgentPlanChange ToAgent(PlanOperationView c, Dictionary<long, string> names) => new(
        AgentLinks.Ref("operation", c.Id), names.GetValueOrDefault(c.PlanId) ?? AgentLinks.Ref("plan", c.PlanId), c.Kind, c.Summary,
        c.Problem, c.Conflicts, c.Blocked);

    private static long ParseRef(string reference, string type)
    {
        var text = reference.StartsWith(type + ":", StringComparison.Ordinal) ? reference[(type.Length + 1)..] : reference;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new McpException($"\"{reference}\" is not a {type} reference like \"{type}:12\".");
    }

    private static (string Type, long Id) ParseObject(string reference)
    {
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        var type = colon > 0 ? reference[..colon] : "";
        return type is "site" or "equipment" or "cable"
            ? (type, ParseRef(reference, type))
            : throw new McpException($"\"{reference}\" is not a reference like \"site:12\", \"equipment:34\" or \"cable:56\".");
    }
}

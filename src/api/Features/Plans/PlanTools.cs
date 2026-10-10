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

/// <param name="Target">The object the change is about; for a create, the planned object's ref (negative id) to use later.</param>
public sealed record AgentPlanChange(string Ref, string Plan, string Kind, string Summary, string? Problem, IReadOnlyList<string> Conflicts,
    bool Blocked, string? Target = null);

/// <summary>A plan's view as an agent sees it: every change against production, what does not fit, and the link for a person.</summary>
public sealed record AgentPlanPreview(AgentPlan Plan, IReadOnlyList<AgentPlanChange> Changes, int Problems, int Conflicts,
    bool ReadyToApply, string Note);

public sealed record AgentPlanAdded(AgentPlan Plan, IReadOnlyList<AgentPlanChange> Added);

/// <summary>One change for <c>add_to_plan</c>.</summary>
public sealed class AgentPlanOperation
{
    [Description("connect, disconnect, set_lifecycle, rename, create_site, create_equipment, create_cable, split_cable (insert a site into a cable) " +
        "remove (a site, equipment or cable that carries no circuits; a site takes its equipment and cables along) " +
        "set_classification (a classification level on a site, equipment, cable or service) " +
        "move (equipment to another rack or site, or a cable's end to another site; connections on what changes site go and nothing " +
        "that carries a circuit can move) or set_conductor_usage (fibres of a cable as dark, dark_fibre (leased) or spare; lit is derived).")]
    public string Kind { get; set; } = "";

    [Description("connect/disconnect: the first terminal id (ports and conductor ends have terminal ids in get_object).")]
    public long? A { get; set; }

    [Description("connect/disconnect: the second terminal id.")]
    public long? B { get; set; }

    [Description("connect: patch, splice, termination or internal. Default patch.")]
    public string? ConnectionKind { get; set; }

    [Description("set_lifecycle/rename/remove/set_classification: a reference \"site:12\", \"equipment:34\" or \"cable:56\" (rename: site or equipment; set_classification also \"service:78\").")]
    public string? Target { get; set; }

    [Description("set_lifecycle: planned, under_construction, in_service, decommissioning or removed.")]
    public string? Lifecycle { get; set; }

    [Description("rename, create_site, create_equipment: the name.")]
    public string? Name { get; set; }

    [Description("create_site: a new, unique site code.")]
    public string? Code { get; set; }

    [Description("create_site: hub, aggregation, radio, cabinet or splice.")]
    public string? SiteType { get; set; }

    [Description("create_site: position in SWEREF 99 TM (EPSG:3006), metres east.")]
    public double? X { get; set; }

    [Description("create_site: metres north.")]
    public double? Y { get; set; }

    [Description("create_equipment: an equipment model key; create_cable: a cable type key (describe_catalog).")]
    public string? TypeKey { get; set; }

    [Description("create_equipment: the site, \"site:12\", or a planned one from this plan (its ref in the answer, e.g. \"site:-3\").")]
    public string? Site { get; set; }

    [Description("create_cable: the site at the A end, existing or planned.")]
    public string? ASite { get; set; }

    [Description("create_cable: the site at the B end.")]
    public string? BSite { get; set; }

    [Description("create_equipment: the rack it sits in (by name); the site's first rack when left out, created when missing.")]
    public string? Rack { get; set; }

    [Description("create_equipment: the room the rack stands in, created when missing.")]
    public string? Room { get; set; }

    [Description("create_equipment: its lowest rack unit; checked against the rack's height and what sits there. Left out, it goes on top.")]
    public int? Position { get; set; }

    [Description("set_classification: the schema (see describe_classifications), e.g. \"criticality\"; target is the object, \"site:12\", " +
        "\"equipment:34\", \"cable:56\" or \"service:78\".")]
    public string? Schema { get; set; }

    [Description("set_classification: the level; leave out to clear the classification.")]
    public int? Level { get; set; }

    [Description("split_cable: the cable to insert the site (Site) into, \"cable:56\". Its conductors are spliced through in the site, " +
        "connections move to the new cables' ends and circuits keep running.")]
    public string? Cable { get; set; }

    [Description("split_cable: conductor numbers to terminate in the site instead of splicing through; they must not carry circuits.")]
    public int[]? Terminate { get; set; }

    [Description("move a cable: the end that moves, \"A\" or \"B\"; target is \"cable:56\" and site the site it moves to. " +
        "move equipment: target is \"equipment:34\", site the site it moves to (its own for a move within it), and rack, room and position say where.")]
    public string? End { get; set; }

    [Description("set_conductor_usage: the conductor numbers; target is the cable, \"cable:56\".")]
    public int[]? Conductors { get; set; }

    [Description("set_conductor_usage: dark, dark_fibre (leased, lit by the customer) or spare; leave out to clear it.")]
    public string? Usage { get; set; }
}

/// <summary>
/// Writing through plans (#64, ADR-0005, ADR-0011): agents propose changes as plans that a person reviews and brings
/// into production in the web app. There is no tool that writes to production, and no tool that applies a plan.
/// </summary>
[McpServerToolType]
public sealed class PlanTools(RequestDb db, PlanWrites writes, PlanViews views, GraphHolder holder, ScopeMasks masks, AgentLinks links,
    PlanPatterns patterns, PlanImport import, RoutePlanner routes, IHttpContextAccessor http)
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
                Code = op.Code,
                SiteType = op.SiteType,
                X = op.X,
                Y = op.Y,
                TypeKey = op.TypeKey,
                SiteId = op.Site is null ? null : ParseRef(op.Site, "site", planned: true),
                ASiteId = op.ASite is null ? null : ParseRef(op.ASite, "site", planned: true),
                BSiteId = op.BSite is null ? null : ParseRef(op.BSite, "site", planned: true),
                CableId = op.Cable is null ? null : ParseRef(op.Cable, "cable", planned: false),
                Schema = op.Schema,
                Level = op.Level,
                Rack = op.Rack,
                Room = op.Room,
                Position = op.Position,
                Terminate = op.Terminate,
                End = op.End,
                Conductors = op.Conductors,
                Usage = op.Usage,
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
        var a = await PortRunAsync(planId, from, fromPort, count, ct);
        var b = await PortRunAsync(planId, to, toPort, count, ct);
        return await AddAllAsync(planId, [.. a.Zip(b, (x, y) => new AddOperationRequest
        {
            Id = planId,
            Kind = "connect",
            A = x,
            B = y,
            ConnectionKind = connectionKind,
        })], ct);
    }

    [McpServerTool(Name = "list_site_templates", Title = "Sitemallar", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Site templates for create_site_from_template: a site type with its equipment and internal cabling.")]
    public IReadOnlyList<TemplateSummary> ListSiteTemplates() =>
        [.. Cmdb.Catalog.SiteTemplates.Current.All.OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new TemplateSummary(t.Key, t.Name, t.SiteType, t.Description, t.Equipment.Count, t.Connections.Count))];

    [McpServerTool(Name = "create_site_from_template", Title = "Site från mall", Destructive = false, OpenWorld = false)]
    [Description("Adds a whole site to a draft plan from a template: the site at a position, its equipment in racks and the " +
        "internal cabling, all checked as ordinary changes. All or nothing.")]
    public async Task<AgentPlanAdded> CreateSiteFromTemplate(
        [Description("The plan, \"plan:12\".")] string plan,
        [Description("A template key from list_site_templates.")] string template,
        [Description("A new, unique site code, e.g. \"RAD-001234\".")] string code,
        [Description("The site's name.")] string name,
        [Description("Position in SWEREF 99 TM (EPSG:3006), metres east.")] double x,
        [Description("Metres north.")] double y,
        CancellationToken ct = default)
    {
        RequireWriter();
        var planId = ParseRef(plan, "plan");
        var result = await patterns.TemplateAsync(Http.User, Http.Scope(),
            new TemplateRequest { Id = planId, TemplateKey = template, Code = code, Name = name, X = x, Y = y }, ct);
        return await AddedAsync(planId, result);
    }

    [McpServerTool(Name = "import_to_plan", Title = "Importera till plan", Destructive = false, OpenWorld = false)]
    [Description("Adds many sites and cables to a draft plan from CSV or GeoJSON text, e.g. a rollout of a hundred radio sites. " +
        "CSV: a header row with kind (site or cable), code, name, template (a key from list_site_templates) or siteType, x/y in " +
        "SWEREF 99 TM or lat/lon in WGS 84, and for cables a, b (site codes) and cableType. GeoJSON: points are sites and line " +
        "strings cables, with the same names as properties. Everything is checked first and either all of it goes in or none; " +
        "problems come back per row. Use dryRun to check without writing. Up to 20 000 rows.")]
    public async Task<ImportResult> ImportToPlan(
        [Description("The plan, \"plan:12\".")] string plan,
        [Description("The file's text.")] string content,
        [Description("csv or geojson. Default csv.")] string format = "csv",
        [Description("Only check and count; write nothing.")] bool dryRun = false,
        CancellationToken ct = default)
    {
        RequireWriter();
        var planId = ParseRef(plan, "plan");
        var result = await import.ImportAsync(Http.User, Http.Scope(),
            new ImportRequest { Id = planId, Format = format, Content = content, DryRun = dryRun }, ct);
        return result.Value ?? throw new McpException(result.Error!);
    }

    [McpServerTool(Name = "suggest_route", Title = "Föreslå väg", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Suggests how to connect two sites over existing cables that still have free fibres: up to three alternatives, " +
        "shortest first, each with the sites and cables it runs over and where fibres are spliced through. When there is no way " +
        "it suggests a new cable between the closest sites of the two sides. Calculated on cables and sites, not ducts. " +
        "Nothing is written; add_route_to_plan puts an alternative in a plan.")]
    public async Task<RouteSuggestion> SuggestRoute(
        [Description("The start site: its code (e.g. \"HUB-001\") or \"site:12\".")] string from,
        [Description("The goal site.")] string to,
        [Description("How many fibres the connection needs, 1–96. Default 1.")] int fibres = 1,
        [Description("A plan, \"plan:12\": count what that plan already uses as taken.")] string? plan = null,
        CancellationToken ct = default)
    {
        var result = await routes.SuggestAsync(Http.Scope(),
            new RouteRequest { From = SiteKey(from), To = SiteKey(to), Fibres = fibres, Plan = plan is null ? null : ParseRef(plan, "plan") }, ct);
        return result.Value ?? throw new McpException(result.Error ?? "Hittade inte siterna.");
    }

    [McpServerTool(Name = "add_route_to_plan", Title = "Lägg väg i plan", Destructive = false, OpenWorld = false)]
    [Description("Adds one of the alternatives from suggest_route to a draft plan: a new cable if it needs one, and splices of free " +
        "fibres through each site on the way, all or nothing. The ends are left for terminate_cable or connect_ports.")]
    public async Task<AgentPlanAdded> AddRouteToPlan(
        [Description("The plan, \"plan:12\".")] string plan,
        [Description("The start site: its code or \"site:12\".")] string from,
        [Description("The goal site.")] string to,
        [Description("How many fibres, 1–96. Default 1.")] int fibres = 1,
        [Description("Which alternative from suggest_route, from 0. Default 0.")] int alternative = 0,
        CancellationToken ct = default)
    {
        RequireWriter();
        var planId = ParseRef(plan, "plan");
        return await AddedAsync(planId, await routes.AddAsync(Http.User, Http.Scope(),
            new AddRouteRequest { Id = planId, From = SiteKey(from), To = SiteKey(to), Fibres = fibres, Alternative = alternative }, ct));
    }

    private static string SiteKey(string site) => site.StartsWith("site:", StringComparison.Ordinal) ? site["site:".Length..] : site;

    [McpServerTool(Name = "splice_ports_to_cable", Title = "Mönsterpatchning", Destructive = false, OpenWorld = false)]
    [Description("Pattern patching: ports from a start port (every portStep-th) to fibres from a start fibre (every conductorStep-th) " +
        "on one side of a cable, e.g. ODF ports 1–12 to fibres 13–24 at the A end. Equipment and cable may be planned. All or nothing.")]
    public async Task<AgentPlanAdded> SplicePortsToCable(
        [Description("The plan, \"plan:12\".")] string plan,
        [Description("Equipment, \"equipment:34\" (or planned, \"equipment:-5\").")] string equipment,
        [Description("First port: name or position.")] string fromPort,
        [Description("Cable, \"cable:56\" (or planned).")] string cable,
        [Description("How many connections, 1–288.")] int count,
        [Description("The cable end at the equipment's site: A or B.")] string side = "A",
        [Description("First fibre (conductor number).")] int fromConductor = 1,
        [Description("Take every n-th port.")] int portStep = 1,
        [Description("Take every n-th fibre.")] int conductorStep = 1,
        [Description("splice, termination or patch.")] string kind = "splice",
        CancellationToken ct = default)
    {
        RequireWriter();
        var planId = ParseRef(plan, "plan");
        var result = await patterns.PatternAsync(Http.User, Http.Scope(), new PatternRequest
        {
            Id = planId,
            EquipmentId = ParseRef(equipment, "equipment", planned: true),
            FromPort = fromPort,
            Count = count,
            PortStep = portStep,
            CableId = ParseRef(cable, "cable", planned: true),
            FromConductor = fromConductor,
            ConductorStep = conductorStep,
            Side = side,
            Kind = kind,
        }, ct);
        return await AddedAsync(planId, result);
    }

    [McpServerTool(Name = "terminate_cable", Title = "Terminera kabel", Destructive = false, OpenWorld = false)]
    [Description("Terminates a cable in a draft plan at both ends as the plan suggests: at each end's site, the ODF with the most free " +
        "ports at the back, fibre k to its k-th free port. Use after adding a cable.")]
    public async Task<AgentPlanAdded> TerminateCable(
        [Description("The plan, \"plan:12\".")] string plan,
        [Description("The cable, \"cable:56\" (or planned).")] string cable,
        CancellationToken ct = default)
    {
        RequireWriter();
        var planId = ParseRef(plan, "plan");
        var termination = await patterns.TerminationAsync(Http.Scope(), planId, ParseRef(cable, "cable", planned: true), ct)
            ?? throw new McpException($"No {cable} in plan:{planId}.");
        var operations = termination.Sides.SelectMany(s => s.Operations).ToList();
        if (operations.Count == 0)
        {
            throw new McpException("Nothing to terminate: no free ODF ports at the ends, or the fibres are already spliced.");
        }
        return await AddedAsync(planId, await patterns.BatchAsync(Http.User, Http.Scope(), planId, operations, ct));
    }

    private async Task<AgentPlanAdded> AddedAsync(long planId, PlanWrite<List<PlanOperationView>> result)
    {
        if (result.Value is null)
        {
            throw new McpException(result.Error!);
        }
        var summary = (await PlanSql.SummariesAsync(db, [planId], CancellationToken.None)).Single();
        var names = new Dictionary<long, string> { [planId] = summary.Name };
        return new AgentPlanAdded(ToAgent(summary), [.. result.Value.Select(a => ToAgent(a, names))]);
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
    private async Task<List<long>> PortRunAsync(long planId, string equipment, string start, int count, CancellationToken ct)
    {
        var scope = Http.Scope();
        long id;
        if (equipment.StartsWith("equipment:", StringComparison.Ordinal))
        {
            id = ParseRef(equipment, "equipment", planned: true);
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
        var ports = new List<(long Terminal, string Name, int Position)>();
        if (id < 0)
        {
            // Planned equipment (#107): its ports come from the model's template, with planned terminal ids.
            var chain = await PlanViews.LoadChainAsync(db, planId, ct);
            var op = chain?.Operations.FirstOrDefault(o => o.Kind == "create_equipment" && Planned.ObjectId(o.Id) == id)
                ?? throw new McpException($"No planned equipment:{id} in plan:{planId}.");
            var type = Cmdb.Catalog.TypeCatalog.Current.Find(op.Payload.GetProperty("typeKey").GetString()!)!;
            ports.AddRange(Cmdb.Catalog.PortExpansion.Expand(type).Select(p => (Planned.Terminal(op.Id, p.Position), p.Name, p.Position)));
            return Run(ports, id, start, count);
        }
        await using var cmd = db.CreateCommand($"""
            SELECT p.terminal_id, p.name, p.position FROM port p JOIN equipment e ON e.id = p.equipment_id
            WHERE e.id = $1 AND {ScopeSql.Site("e.site_id", 2)} ORDER BY p.position
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(scope.Parameter());
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                ports.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }
        return Run(ports, id, start, count);
    }

    private static List<long> Run(List<(long Terminal, string Name, int Position)> ports, long id, string start, int count)
    {
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
        c.Problem, c.Conflicts, c.Blocked, c.Target is { Id: not 0 } t ? AgentLinks.Ref(t.Type, t.Id) : null);

    /// <param name="planned">Also accept a planned object's negative id (#107).</param>
    private static long ParseRef(string reference, string type, bool planned = false)
    {
        var text = reference.StartsWith(type + ":", StringComparison.Ordinal) ? reference[(type.Length + 1)..] : reference;
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) && (id > 0 || (planned && id < 0))
            ? id
            : throw new McpException($"\"{reference}\" is not a {type} reference like \"{type}:12\".");
    }

    private static (string Type, long Id) ParseObject(string reference)
    {
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        var type = colon > 0 ? reference[..colon] : "";
        return type is "site" or "equipment" or "cable" or "service"
            // A negative id is a site or equipment the plan creates (#179); the write checks that it exists in the plan.
            ? (type, ParseRef(reference, type, planned: true))
            : throw new McpException($"\"{reference}\" is not a reference like \"site:12\", \"equipment:34\", \"cable:56\" or \"service:78\".");
    }
}

using System.Diagnostics;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Trace;
using Cmdb.Graph;
using FastEndpoints;
using FluentValidation;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

public sealed record PlanSummary(long Id, string Name, string Description, string Status, string? Flag, string CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? AppliedBy, DateTimeOffset? AppliedAt, IReadOnlyList<long> DependsOn,
    int Operations, int Conflicts = 0);

/// <summary>An operation in words, with the objects it touches and, when it no longer fits, why.</summary>
/// <param name="Conflicts">Claims on the same resources by others (#25): a reservation (blocking) or another plan.</param>
/// <param name="Blocked">A reservation by someone else stops the plan from being applied.</param>
public sealed record PlanOperationView(long Id, long PlanId, int Seq, string Kind, string Summary, IReadOnlyList<TraceHop> Terminals,
    ObjectRef? Target, string? ConnectionKind, string? Lifecycle, string? Name, string? Problem, string CreatedBy, DateTimeOffset CreatedAt,
    IReadOnlyList<string> Conflicts, bool Blocked);

public sealed record PlanDetail(PlanSummary Plan, IReadOnlyList<PlanSummary> Dependencies, IReadOnlyList<PlanOperationView> Operations);

public sealed record PlanSite(long Id, double X, double Y);

/// <summary>
/// The plan's view as a diff against production (#24): every operation of the plan and the draft plans under it, in
/// the order they apply, with the sites they touch for the map.
/// </summary>
public sealed record PlanDiff(PlanSummary Plan, IReadOnlyList<PlanSummary> Plans, IReadOnlyList<PlanOperationView> Changes,
    IReadOnlyList<PlanSite> Sites, double[]? Extent, int Problems, double ElapsedMs);

public sealed record PlanIdRequest(long Id);

public sealed class CreatePlanRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long[]? DependsOn { get; set; }
}

public sealed class CreatePlanValidator : Validator<CreatePlanRequest>
{
    public CreatePlanValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Description).MaximumLength(4000);
        RuleFor(r => r.DependsOn).Must(d => d is null || d.Length <= 50).WithMessage("At most 50 dependencies.");
    }
}

public sealed class SetDependenciesRequest
{
    public long Id { get; set; }
    public long[] DependsOn { get; set; } = [];
}

public sealed class AddOperationRequest
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public long? A { get; set; }
    public long? B { get; set; }
    public string? ConnectionKind { get; set; }
    public string? Type { get; set; }
    public long? ObjectId { get; set; }
    public string? Lifecycle { get; set; }
    public string? Name { get; set; }
}

public sealed class AddOperationValidator : Validator<AddOperationRequest>
{
    private static readonly string[] Lifecycles = ["planned", "under_construction", "in_service", "decommissioning", "removed"];

    public AddOperationValidator()
    {
        RuleFor(r => r.Kind).Must(k => PlanKinds.Operations.Contains(k)).WithMessage("kind is connect, disconnect, set_lifecycle or rename.");
        When(r => r.Kind is "connect" or "disconnect", () =>
        {
            RuleFor(r => r.A).NotNull();
            RuleFor(r => r.B).NotNull().NotEqual(r => r.A).WithMessage("a and b are two different terminals.");
        });
        When(r => r.Kind == "connect", () =>
            RuleFor(r => r.ConnectionKind).Must(k => PlanKinds.Connections.Contains(k)).WithMessage("connectionKind is patch, splice, termination or internal."));
        When(r => r.Kind == "set_lifecycle", () =>
        {
            RuleFor(r => r.Type).Must(t => t is "site" or "equipment" or "cable").WithMessage("type is site, equipment or cable.");
            RuleFor(r => r.ObjectId).NotNull();
            RuleFor(r => r.Lifecycle).Must(l => Lifecycles.Contains(l)).WithMessage("Unknown lifecycle.");
        });
        When(r => r.Kind == "rename", () =>
        {
            RuleFor(r => r.Type).Must(t => t is "site" or "equipment").WithMessage("type is site or equipment.");
            RuleFor(r => r.ObjectId).NotNull();
            RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        });
    }
}

public sealed class DeleteOperationRequest
{
    public long Id { get; set; }
    public long OperationId { get; set; }
}

/// <summary>Plans visible to the caller (#24): drafts first, most recently changed first.</summary>
public sealed class ListPlansEndpoint(RequestDb db) : EndpointWithoutRequest<List<PlanSummary>>
{
    public override void Configure() => Get("/plans");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var plans = await PlanSql.SummariesAsync(db, null, ct);
        await Send.OkAsync([.. plans.Where(p => scope.SeesPlan(p.Id))
            .OrderBy(p => p.Status == "draft" ? 0 : 1).ThenByDescending(p => p.UpdatedAt)], ct);
    }
}

/// <summary>Creates a draft plan on top of production and, optionally, other plans.</summary>
public sealed class CreatePlanEndpoint(RequestDb db) : Endpoint<CreatePlanRequest, PlanSummary>
{
    public override void Configure()
    {
        Post("/plans");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(CreatePlanRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var dependsOn = (req.DependsOn ?? []).Distinct().ToArray();
        if (await PlanSql.CheckDependenciesAsync(db, dependsOn, scope, ct) is { } problem)
        {
            AddError(r => r.DependsOn!, problem);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand("INSERT INTO plan (name, description, created_by) VALUES ($1, $2, $3) RETURNING id", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = req.Name });
            cmd.Parameters.Add(new() { Value = req.Description ?? "" });
            cmd.Parameters.Add(new() { Value = PlanSql.Actor(User) });
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await PlanSql.InsertDependenciesAsync(conn, tx, id, dependsOn, ct);
        await tx.CommitAsync(ct);
        var created = (await PlanSql.SummariesAsync(db, [id], ct)).Single();
        await Send.CreatedAtAsync<GetPlanEndpoint>(new { id }, created, cancellation: ct);
    }
}

/// <summary>A plan with its dependencies and its own operations, each checked against the plan's view.</summary>
public sealed class GetPlanEndpoint(RequestDb db, GraphHolder holder, PlanViews views, ScopeMasks masks) : Endpoint<PlanIdRequest, PlanDetail>
{
    public override void Configure() => Get("/plans/{id}");

    public override async Task HandleAsync(PlanIdRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var graph = holder.Require();
        if (await views.GetAsync(graph, req.Id, scope, ct) is not { } view)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var summaries = await PlanSql.SummariesAsync(db, [req.Id, .. view.Chain.Plan.DependsOn], ct);
        var own = await PlanSql.OperationsAsync(db, req.Id, ct);
        var mask = await masks.GetAsync(graph, scope, ct);
        var operations = await PlanSql.DescribeAsync(db, graph, mask, scope, own, view.Problems, ct);
        await Send.OkAsync(new PlanDetail(
            summaries.Single(s => s.Id == req.Id),
            [.. summaries.Where(s => s.Id != req.Id && scope.SeesPlan(s.Id))],
            operations), ct);
    }
}

/// <summary>Replaces the plan's dependencies; a dependency that would close a cycle is refused.</summary>
public sealed class SetDependenciesEndpoint(RequestDb db) : Endpoint<SetDependenciesRequest, PlanSummary>
{
    public override void Configure()
    {
        Put("/plans/{id}/dependencies");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(SetDependenciesRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (!scope.SeesPlan(req.Id) || (await PlanSql.SummariesAsync(db, [req.Id], ct)).SingleOrDefault() is not { } plan)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (plan.Status != "draft")
        {
            await PlanSql.ConflictAsync(HttpContext, "Bara utkast kan ändras.", ct);
            return;
        }
        var dependsOn = req.DependsOn.Distinct().ToArray();
        var problem = dependsOn.Contains(req.Id)
            ? "En plan kan inte bero på sig själv."
            : await PlanSql.CheckDependenciesAsync(db, dependsOn, scope, ct);
        if (problem is null && await PlanSql.WouldCycleAsync(db, req.Id, dependsOn, ct))
        {
            problem = "Beroendet skulle ge en cykel: planen bygger redan på den här planen.";
        }
        if (problem is not null)
        {
            AddError(r => r.DependsOn, problem);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("DELETE FROM plan_dependency WHERE plan_id = $1", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await PlanSql.InsertDependenciesAsync(conn, tx, req.Id, dependsOn, ct);
        await PlanSql.TouchAsync(conn, tx, req.Id, ct);
        await tx.CommitAsync(ct);
        await Send.OkAsync((await PlanSql.SummariesAsync(db, [req.Id], ct)).Single(), ct);
    }
}

/// <summary>Adds an operation at the end of a draft plan. Only objects inside the caller's scopes can be touched.</summary>
public sealed class AddOperationEndpoint(RequestDb db, GraphHolder holder, PlanViews views, ScopeMasks masks)
    : Endpoint<AddOperationRequest, PlanOperationView>
{
    public override void Configure()
    {
        Post("/plans/{id}/operations");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(AddOperationRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var graph = holder.Require();
        if (await views.GetAsync(graph, req.Id, scope, ct) is not { } view)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (view.Chain.Plan.Status != "draft")
        {
            await PlanSql.ConflictAsync(HttpContext, "Bara utkast kan ändras.", ct);
            return;
        }
        var mask = await masks.GetAsync(graph, scope, ct);
        string payload;
        if (req.Kind is "connect" or "disconnect")
        {
            // Terminals must exist in production and be inside the caller's scopes.
            foreach (var terminal in new[] { req.A!.Value, req.B!.Value })
            {
                if (!graph.TryGetNode(terminal, out var node) || !Visible(graph, mask, node))
                {
                    AddError($"Terminal {terminal} finns inte.");
                    await Send.ErrorsAsync(cancellation: ct);
                    return;
                }
            }
            payload = System.Text.Json.JsonSerializer.Serialize(req.Kind == "connect"
                ? (object)new { a = req.A, b = req.B, kind = req.ConnectionKind }
                : new { a = req.A, b = req.B });
        }
        else
        {
            if (!await PlanSql.ObjectVisibleAsync(db, req.Type!, req.ObjectId!.Value, scope, ct))
            {
                AddError($"{req.Type} {req.ObjectId} finns inte.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            payload = System.Text.Json.JsonSerializer.Serialize(req.Kind == "set_lifecycle"
                ? (object)new { type = req.Type, id = req.ObjectId, lifecycle = req.Lifecycle }
                : new { type = req.Type, id = req.ObjectId, name = req.Name });
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long opId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO plan_operation (plan_id, seq, kind, payload, created_by)
            SELECT $1, coalesce(max(seq), 0) + 1, $2, $3::jsonb, $4 FROM plan_operation WHERE plan_id = $1
            RETURNING id
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            cmd.Parameters.Add(new() { Value = req.Kind });
            cmd.Parameters.Add(new() { Value = payload });
            cmd.Parameters.Add(new() { Value = PlanSql.Actor(User) });
            opId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await PlanSql.TouchAsync(conn, tx, req.Id, ct);
        await tx.CommitAsync(ct);

        var after = (await views.GetAsync(graph, req.Id, scope, ct))!;
        var op = after.Chain.Operations.Single(o => o.Id == opId);
        await Send.OkAsync((await PlanSql.DescribeAsync(db, graph, mask, scope, [op], after.Problems, ct)).Single(), ct);
    }

    private static bool Visible(Cmdb.Graph.Graph g, GraphMask mask, int node)
    {
        var site = g.SiteIndexOfNode(node);
        return site >= 0 ? mask.Sites[site] : mask.Cables[g.CableIndexOfNode(node)];
    }
}

/// <summary>Removes an operation from a draft plan.</summary>
public sealed class DeleteOperationEndpoint(RequestDb db) : Endpoint<DeleteOperationRequest>
{
    public override void Configure()
    {
        Delete("/plans/{id}/operations/{operationId}");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(DeleteOperationRequest req, CancellationToken ct)
    {
        if (!HttpContext.Scope().SeesPlan(req.Id) || (await PlanSql.SummariesAsync(db, [req.Id], ct)).SingleOrDefault() is not { } plan)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (plan.Status != "draft")
        {
            await PlanSql.ConflictAsync(HttpContext, "Bara utkast kan ändras.", ct);
            return;
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int deleted;
        await using (var cmd = new NpgsqlCommand("DELETE FROM plan_operation WHERE plan_id = $1 AND id = $2", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            cmd.Parameters.Add(new() { Value = req.OperationId });
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        }
        if (deleted == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await PlanSql.TouchAsync(conn, tx, req.Id, ct);
        await tx.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

/// <summary>
/// Switching the view to a plan (#24): the diff against production and the sites it touches, from the cached plan
/// view. Budget: 100 ms (docs/plan.md).
/// </summary>
public sealed class PlanViewEndpoint(RequestDb db, GraphHolder holder, PlanViews views, ScopeMasks masks) : Endpoint<PlanIdRequest, PlanDiff>
{
    public override void Configure() => Get("/plans/{id}/view");

    public override async Task HandleAsync(PlanIdRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var scope = HttpContext.Scope();
        var graph = holder.Require();
        if (await views.GetAsync(graph, req.Id, scope, ct) is not { } view)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var mask = await masks.GetAsync(graph, scope, ct);
        var changes = await PlanSql.DescribeAsync(db, graph, mask, scope, view.Chain.Operations, view.Problems, ct);
        var ids = view.Chain.Plans.Select(p => p.Id).ToArray();
        var summaries = await PlanSql.SummariesAsync(db, ids, ct);
        var siteIds = changes.SelectMany(c => c.Terminals.Select(t => t.Site?.Id ?? 0))
            .Concat(changes.Where(c => c.Target?.Type == "site").Select(c => c.Target!.Id))
            .Where(id => id != 0).Distinct().ToArray();
        var sites = scope.HidesCoordinates ? [] : await PlanSql.SitePositionsAsync(db, siteIds, ct);
        double[]? extent = sites.Count == 0 ? null : [sites.Min(s => s.X), sites.Min(s => s.Y), sites.Max(s => s.X), sites.Max(s => s.Y)];
        await Send.OkAsync(new PlanDiff(
            summaries.Single(s => s.Id == req.Id),
            [.. ids.Select(id => summaries.Single(s => s.Id == id)).Where(s => scope.SeesPlan(s.Id))],
            changes,
            sites,
            extent,
            changes.Count(c => c.Problem is not null),
            Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ct);
    }
}

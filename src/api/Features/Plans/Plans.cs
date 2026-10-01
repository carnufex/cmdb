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
    int Operations, int Conflicts = 0, string CreatedVia = "api", string? Client = null);

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
    IReadOnlyList<PlanSite> Sites, double[]? Extent, int Problems, double ElapsedMs, PlannedMap? Planned = null);

/// <summary>What the plan creates (#107), for the map: planned sites and cables with their geometry.</summary>
public sealed record PlannedMap(IReadOnlyList<PlannedSite> Sites, IReadOnlyList<PlannedCable> Cables);

public sealed record PlannedSite(long Id, string Code, string Name, string SiteType, double X, double Y);

public sealed record PlannedCable(long Id, string Code, double[][] Coordinates);

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

    // create_site, create_equipment and create_cable (#107).
    public string? Code { get; set; }
    public string? SiteType { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public string? TypeKey { get; set; }

    /// <summary>create_equipment: the site, existing or planned (negative id).</summary>
    public long? SiteId { get; set; }

    /// <summary>create_equipment: the rack it sits in, created in a building on the site when missing (#26).</summary>
    public string? Rack { get; set; }

    /// <summary>set_attributes: keys to set; a null value removes the key (#27).</summary>
    public System.Text.Json.JsonElement? Attributes { get; set; }

    /// <summary>create_cable: the sites at the two ends, existing or planned.</summary>
    public long? ASiteId { get; set; }
    public long? BSiteId { get; set; }

    /// <summary>split_cable (#168): the cable the site (<see cref="SiteId"/>, existing or planned) is inserted into.</summary>
    public long? CableId { get; set; }

    /// <summary>split_cable: conductor numbers terminated in the site instead of spliced through.</summary>
    public int[]? Terminate { get; set; }
}

public sealed class AddOperationValidator : Validator<AddOperationRequest>
{
    private static readonly string[] Lifecycles = ["planned", "under_construction", "in_service", "decommissioning", "removed"];

    public AddOperationValidator()
    {
        RuleFor(r => r.Kind).Must(k => PlanKinds.Operations.Contains(k)).WithMessage($"kind is one of {string.Join(", ", PlanKinds.Operations)}.");
        When(r => r.Kind == "set_attributes", () =>
        {
            RuleFor(r => r.Type).Must(t => t is "site" or "equipment").WithMessage("type is site or equipment.");
            RuleFor(r => r.ObjectId).NotNull();
            RuleFor(r => r.Attributes).Must(a => a is { ValueKind: System.Text.Json.JsonValueKind.Object } o
                    && o.EnumerateObject().Count() is > 0 and <= 50
                    && o.EnumerateObject().All(p => p.Name.Length <= 100 && p.Value.ValueKind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array)))
                .WithMessage("attributes is an object of 1–50 keys with plain values; null removes a key.");
        });
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
        When(r => r.Kind == "split_cable", () =>
        {
            RuleFor(r => r.CableId).NotNull();
            RuleFor(r => r.SiteId).NotNull().NotEqual(0);
            RuleFor(r => r.Terminate).Must(t => t is null || t.Length <= CableSplit.MaxConductors).WithMessage("Too many conductors to terminate.");
        });
        When(r => r.Kind == "create_site", () =>
        {
            RuleFor(r => r.Code).NotEmpty().MaximumLength(50);
            RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
            RuleFor(r => r.SiteType).Must(t => PlanKinds.SiteTypes.Contains(t)).WithMessage("siteType is hub, aggregation, radio, cabinet or splice.");
            RuleFor(r => r.X).NotNull().InclusiveBetween(Map.TileGrid.MinX, Map.TileGrid.MaxX);
            RuleFor(r => r.Y).NotNull().InclusiveBetween(Map.TileGrid.MinY, Map.TileGrid.MaxY);
        });
        When(r => r.Kind == "create_equipment", () =>
        {
            RuleFor(r => r.SiteId).NotNull().NotEqual(0);
            RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
            RuleFor(r => r.Rack).MaximumLength(100);
            RuleFor(r => r.TypeKey).Must(k => k is not null && Cmdb.Catalog.TypeCatalog.Embedded.Find(k) is { Category: not "card" })
                .WithMessage("typeKey is an equipment model that is not a card (describe_catalog lists them).");
        });
        When(r => r.Kind == "create_cable", () =>
        {
            RuleFor(r => r.ASiteId).NotNull().NotEqual(0);
            RuleFor(r => r.BSiteId).NotNull().NotEqual(0).NotEqual(r => r.ASiteId).WithMessage("A cable joins two different sites.");
            RuleFor(r => r.TypeKey).Must(k => Planned.ConductorCount(k ?? "") > 0).WithMessage("typeKey is a cable type in the catalog.");
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
public sealed class CreatePlanEndpoint(PlanWrites writes) : Endpoint<CreatePlanRequest, PlanSummary>
{
    public override void Configure()
    {
        Post("/plans");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(CreatePlanRequest req, CancellationToken ct)
    {
        var result = await writes.CreateAsync(User, PlanWrites.Via(HttpContext), HttpContext.Scope(), req.Name, req.Description,
            req.DependsOn ?? [], ct);
        if (result.Value is not { } created)
        {
            AddError(r => r.DependsOn!, result.Error!);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        await Send.CreatedAtAsync<GetPlanEndpoint>(new { id = created.Id }, created, cancellation: ct);
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
        var operations = await PlanSql.DescribeAsync(db, graph, mask, scope, own, view.Problems, ct, view.Chain.Operations);
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
public sealed class AddOperationEndpoint(PlanWrites writes) : Endpoint<AddOperationRequest, PlanOperationView>
{
    public override void Configure()
    {
        Post("/plans/{id}/operations");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(AddOperationRequest req, CancellationToken ct)
    {
        var result = await writes.AddAsync(User, HttpContext.Scope(), req, ct);
        switch (result.Failure)
        {
            case PlanWriteFailure.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanWriteFailure.Conflict:
                await PlanSql.ConflictAsync(HttpContext, result.Error!, ct);
                return;
            case PlanWriteFailure.Invalid:
                AddError(result.Error!);
                await Send.ErrorsAsync(cancellation: ct);
                return;
            default:
                await Send.OkAsync(result.Value!, ct);
                return;
        }
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
        var planned = scope.HidesCoordinates ? null : await PlannedMapAsync(view.Chain.Operations, ct);
        List<PlanSite> sites = scope.HidesCoordinates ? [] : [.. await PlanSql.SitePositionsAsync(db, [.. siteIds.Where(id => id > 0)], ct),
            .. planned!.Sites.Select(s => new PlanSite(s.Id, s.X, s.Y))];
        double[]? extent = sites.Count == 0 ? null : [sites.Min(s => s.X), sites.Min(s => s.Y), sites.Max(s => s.X), sites.Max(s => s.Y)];
        await Send.OkAsync(new PlanDiff(
            summaries.Single(s => s.Id == req.Id),
            [.. ids.Select(id => summaries.Single(s => s.Id == id)).Where(s => scope.SeesPlan(s.Id))],
            changes,
            sites,
            extent,
            changes.Count(c => c.Problem is not null),
            Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            planned), ct);
    }

    /// <summary>Planned sites from their operations, and planned cables as a line between their two sites.</summary>
    private async Task<PlannedMap> PlannedMapAsync(IReadOnlyList<PlanOp> operations, CancellationToken ct)
    {
        var sites = operations.Where(o => o.Kind == "create_site").Select(o => new PlannedSite(Planned.ObjectId(o.Id),
            o.Payload.GetProperty("code").GetString()!, o.Payload.GetProperty("name").GetString()!, o.Payload.GetProperty("siteType").GetString()!,
            o.Payload.GetProperty("x").GetDouble(), o.Payload.GetProperty("y").GetDouble())).ToList();
        var cables = operations.Where(o => o.Kind == "create_cable").ToList();
        // A split cable's two parts (#168), with the geometry worked out when the operation was added.
        List<PlannedCable> parts = [.. operations.Where(o => o.Kind == "split_cable").SelectMany(o => new[]
        {
            new PlannedCable(Planned.ObjectId(o.Id), $"{o.Payload.GetProperty("code").GetString()}-A", Line(o.Payload.GetProperty("lineA"))),
            new PlannedCable(CableSplit.SecondCable(o.Id), $"{o.Payload.GetProperty("code").GetString()}-B", Line(o.Payload.GetProperty("lineB"))),
        })];
        if (cables.Count == 0)
        {
            return new PlannedMap(sites, parts);
        }
        var ends = cables.SelectMany(c => new[] { c.Payload.GetProperty("a").GetInt64(), c.Payload.GetProperty("b").GetInt64() }).ToHashSet();
        var positions = (await PlanSql.SitePositionsAsync(db, [.. ends.Where(id => id > 0)], ct)).ToDictionary(s => s.Id, s => (s.X, s.Y));
        foreach (var s in sites)
        {
            positions[s.Id] = (s.X, s.Y);
        }
        return new PlannedMap(sites, [.. cables
            .Where(c => positions.ContainsKey(c.Payload.GetProperty("a").GetInt64()) && positions.ContainsKey(c.Payload.GetProperty("b").GetInt64()))
            .Select(c =>
            {
                var (a, b) = (positions[c.Payload.GetProperty("a").GetInt64()], positions[c.Payload.GetProperty("b").GetInt64()]);
                return new PlannedCable(Planned.ObjectId(c.Id), $"NY-K{c.Id}", [[a.X, a.Y], [b.X, b.Y]]);
            }), .. parts]);
    }

    private static double[][] Line(System.Text.Json.JsonElement points) =>
        [.. points.EnumerateArray().Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() })];
}

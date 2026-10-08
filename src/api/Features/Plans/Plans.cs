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
    int Operations, int Conflicts = 0, string CreatedVia = "api", string? Client = null, string? Exception = null);



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
/// <param name="Counts">Operations per plan in the chain. A big plan (an import, #170) lists only some of them in
/// <see cref="Changes"/>: the first ones and every one with a problem or conflict; <c>all=true</c> lists them all.</param>
public sealed record PlanDiff(PlanSummary Plan, IReadOnlyList<PlanSummary> Plans, IReadOnlyList<PlanOperationView> Changes,
    IReadOnlyList<PlanSite> Sites, double[]? Extent, int Problems, double ElapsedMs, PlannedMap? Planned = null,
    IReadOnlyDictionary<long, int>? Counts = null);

public sealed class PlanViewRequest
{
    public long Id { get; set; }

    /// <summary>Every operation, not just the first ones and those with problems.</summary>
    public bool All { get; set; }
}

/// <summary>
/// What the plan creates (#107) and removes (#168, #172), for the map: planned sites and cables with their geometry, and the
/// sites and cables it takes away.
/// </summary>
public sealed record PlannedMap(IReadOnlyList<PlannedSite> Sites, IReadOnlyList<PlannedCable> Cables, PlannedRemovals? Removed = null);

public sealed record PlannedRemovals(IReadOnlyList<PlanSite> Sites, IReadOnlyList<PlannedCable> Cables);

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

    /// <summary>create_equipment: the room the rack stands in, created when missing (#173).</summary>
    public string? Room { get; set; }

    /// <summary>create_equipment: its lowest rack unit (#173); without it, it goes on top of what the rack holds.</summary>
    public int? Position { get; set; }

    /// <summary>set_attributes: keys to set; a null value removes the key (#27). create_site, create_cable: the new object's
    /// attributes, checked against its type's schema (#211).</summary>
    public System.Text.Json.JsonElement? Attributes { get; set; }

    /// <summary>create_cable: the sites at the two ends, existing or planned.</summary>
    public long? ASiteId { get; set; }
    public long? BSiteId { get; set; }

    /// <summary>set_classification (#176): the schema (e.g. criticality) and the level; a null level clears it.</summary>
    public string? Schema { get; set; }
    public int? Level { get; set; }

    /// <summary>split_cable (#168): the cable the site (<see cref="SiteId"/>, existing or planned) is inserted into.</summary>
    public long? CableId { get; set; }

    /// <summary>split_cable: conductor numbers terminated in the site instead of spliced through.</summary>
    public int[]? Terminate { get; set; }

    /// <summary>move a cable: the end that moves, A or B.</summary>
    public string? End { get; set; }
}

public sealed class AddOperationValidator : Validator<AddOperationRequest>
{
    private static readonly string[] Lifecycles = ["planned", "under_construction", "in_service", "decommissioning", "removed"];

    /// <summary>1–50 keys whose values are plain or a flat list of plain values (a site's other names, #211).</summary>
    private static bool PlainAttributes(System.Text.Json.JsonElement? attributes) =>
        attributes is { ValueKind: System.Text.Json.JsonValueKind.Object } o
            && o.EnumerateObject().Count() is > 0 and <= 50
            && o.EnumerateObject().All(p => p.Name.Length <= 100 && Plain(p.Value, list: true));

    private static bool Plain(System.Text.Json.JsonElement value, bool list) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => false,
        System.Text.Json.JsonValueKind.Array => list && value.GetArrayLength() <= 100 && value.EnumerateArray().All(v => Plain(v, list: false)),
        _ => true,
    };

    public AddOperationValidator()
    {
        RuleFor(r => r.Kind).Must(k => PlanKinds.Operations.Contains(k)).WithMessage($"kind is one of {string.Join(", ", PlanKinds.Operations)}.");
        When(r => r.Kind == "set_attributes", () =>
        {
            RuleFor(r => r.Type).Must(t => t is "site" or "equipment" or "cable").WithMessage("type is site, equipment or cable.");
            RuleFor(r => r.ObjectId).NotNull();
            RuleFor(r => r.Attributes).Must(PlainAttributes).WithMessage("attributes is an object of 1–50 keys with plain values or lists of them; null removes a key.");
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
        When(r => r.Kind == "set_classification", () =>
        {
            RuleFor(r => r.Type).Must(t => t is "site" or "equipment" or "cable" or "service").WithMessage("type is site, equipment, cable or service.");
            RuleFor(r => r.ObjectId).NotNull().NotEqual(0).WithMessage("objectId is an object, or a site or equipment the plan creates (negative id).");
            RuleFor(r => r.Schema).NotEmpty().MaximumLength(50);
        });
        When(r => r.Kind == "remove", () =>
        {
            RuleFor(r => r.Type).Must(t => t is "site" or "equipment" or "cable").WithMessage("type is site, equipment or cable.");
            RuleFor(r => r.ObjectId).NotNull().GreaterThan(0).WithMessage("objectId is an existing object; remove a planned one by deleting its operation.");
        });
        When(r => r.Kind == "move", () =>
        {
            RuleFor(r => r.Type).Must(t => t is "equipment" or "cable").WithMessage("type is equipment or cable.");
            RuleFor(r => r.ObjectId).NotNull().GreaterThan(0).WithMessage("objectId is an existing object.");
            RuleFor(r => r.SiteId).NotNull().NotEqual(0).WithMessage("site is the site it moves to, existing or planned.");
            RuleFor(r => r.Rack).MaximumLength(100);
            When(r => r.Type == "cable", () =>
                RuleFor(r => r.End).Must(e => e is not null && (e.Equals("A", StringComparison.OrdinalIgnoreCase) || e.Equals("B", StringComparison.OrdinalIgnoreCase)))
                    .WithMessage("end is A or B."));
        });
        When(r => r.Kind == "split_cable", () =>
        {
            RuleFor(r => r.CableId).NotNull();
            RuleFor(r => r.SiteId).NotNull().NotEqual(0);
            RuleFor(r => r.Terminate).Must(t => t is null || t.Length <= CableSplit.MaxConductors).WithMessage("Too many conductors to terminate.");
        });
        When(r => r.Kind is "create_site" or "create_cable" && r.Attributes is not null, () =>
            RuleFor(r => r.Attributes).Must(PlainAttributes).WithMessage("attributes is an object of 1–50 keys with plain values or lists of them."));
        When(r => r.Kind == "create_site", () =>
        {
            RuleFor(r => r.Code).NotEmpty().MaximumLength(50);
            RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
            RuleFor(r => r.SiteType).Must(t => t is not null && Cmdb.Catalog.TypeCatalog.Current.FindSiteType(t) is not null)
                .WithMessage(_ => $"siteType is one of {string.Join(", ", Cmdb.Catalog.TypeCatalog.Current.SiteTypes.Select(t => t.Key))}.");
            RuleFor(r => r.X).NotNull().InclusiveBetween(Map.TileGrid.MinX, Map.TileGrid.MaxX);
            RuleFor(r => r.Y).NotNull().InclusiveBetween(Map.TileGrid.MinY, Map.TileGrid.MaxY);
        });
        When(r => r.Kind == "create_equipment", () =>
        {
            RuleFor(r => r.SiteId).NotNull().NotEqual(0);
            RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
            RuleFor(r => r.Rack).MaximumLength(100);
            RuleFor(r => r.TypeKey).Must(k => k is not null && Cmdb.Catalog.TypeCatalog.Current.Find(k) is not null && !Cmdb.Catalog.TypeCatalog.Current.TypeHas(k, Cmdb.Catalog.CatalogRoles.Card))
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
public sealed class PlanViewEndpoint(RequestDb db, GraphHolder holder, PlanViews views, ScopeMasks masks) : Endpoint<PlanViewRequest, PlanDiff>
{
    public override void Configure() => Get("/plans/{id}/view");

    /// <summary>Operations listed per plan unless all are asked for.</summary>
    public const int Listed = 200;

    public override async Task HandleAsync(PlanViewRequest req, CancellationToken ct)
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
        var listed = view.Chain.Operations;
        if (!req.All && listed.Count > Listed)
        {
            var conflicting = (await Reservations.ClaimsSql.ConflictsAsync(db, [.. view.Chain.Plans.Select(p => p.Id)], ct)).Select(c => c.OperationId).ToHashSet();
            listed = [.. listed.GroupBy(o => o.PlanId).SelectMany(g => g.Where((o, i) =>
                i < Listed || view.Problems.ContainsKey(o.Id) || conflicting.Contains(o.Id)))];
        }
        // Names for what the listed operations refer to: the planned sites, and the planned objects whose terminals they use.
        var context = view.Chain.Operations;
        if (listed.Count < context.Count)
        {
            var needed = listed.Select(o => o.Id).ToHashSet();
            foreach (var o in listed)
            {
                foreach (var id in o.PlannedReferences.Concat(o.Edge is { } e ? [e.A, e.B] : []).Where(id => id < 0))
                {
                    needed.Add(-id < Planned.PerObject ? -id : -id / Planned.PerObject);
                }
            }
            context = [.. context.Where(o => o.Kind == "create_site" || needed.Contains(o.Id))];
        }
        var changes = await PlanSql.DescribeAsync(db, graph, mask, scope, listed, view.Problems, ct, context);
        var ids = view.Chain.Plans.Select(p => p.Id).ToArray();
        var summaries = await PlanSql.SummariesAsync(db, ids, ct);
        var siteIds = changes.SelectMany(c => c.Terminals.Select(t => t.Site?.Id ?? 0))
            .Concat(changes.Where(c => c.Target?.Type == "site").Select(c => c.Target!.Id))
            .Where(id => id != 0).Distinct().ToArray();
        var planned = scope.HidesCoordinates ? null : await PlannedMapAsync(view.Chain.Operations, scope, ct);
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
            planned,
            view.Chain.Operations.GroupBy(o => o.PlanId).ToDictionary(g => g.Key, g => g.Count())), ct);
    }

    /// <summary>Planned sites from their operations, and planned cables as a line between their two sites.</summary>
    private async Task<PlannedMap> PlannedMapAsync(IReadOnlyList<PlanOp> operations, UserScope scope, CancellationToken ct)
    {
        var map = await CreatedAsync(operations, ct);
        return map with { Removed = await RemovedAsync(operations, scope, ct) };
    }

    /// <summary>Sites and cables the plan removes, and the cables a split replaces, as they stand in production.</summary>
    private async Task<PlannedRemovals?> RemovedAsync(IReadOnlyList<PlanOp> operations, UserScope scope, CancellationToken ct)
    {
        var cables = new HashSet<long>();
        var sites = new HashSet<long>();
        foreach (var op in operations)
        {
            if (op.Kind == "split_cable")
            {
                cables.Add(op.Payload.GetProperty("cable").GetInt64());
            }
            else if (op.Kind == "remove")
            {
                cables.UnionWith(ObjectRemoval.Objects(op.Payload).Cables);
                if (op.ObjectType == "site")
                {
                    sites.Add(op.ObjectId);
                }
            }
        }
        if (cables.Count == 0 && sites.Count == 0)
        {
            return null;
        }
        var lines = new List<PlannedCable>();
        await using (var cmd = db.CreateCommand($"""
            SELECT c.id, c.code, ARRAY(SELECT ARRAY[round(ST_X(p.geom)), round(ST_Y(p.geom))]
                                       FROM ST_DumpPoints(ST_Simplify({ScopeSql.CableGeometry("c", 2, scope)}, 10)) p ORDER BY p.path)
            FROM cable c WHERE c.id = ANY($1) AND {ScopeSql.Cable("c.id", 2)}
            """))
        {
            cmd.Parameters.Add(new() { Value = cables.ToArray() });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetValue(2) is double[,] points && points.Length > 0)
                {
                    lines.Add(new PlannedCable(reader.GetInt64(0), reader.GetString(1),
                        [.. Enumerable.Range(0, points.GetLength(0)).Select(i => new[] { points[i, 0], points[i, 1] })]));
                }
            }
        }
        return new PlannedRemovals(await PlanSql.SitePositionsAsync(db, [.. sites], ct), lines);
    }

    private async Task<PlannedMap> CreatedAsync(IReadOnlyList<PlanOp> operations, CancellationToken ct)
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
                // An imported route (#170) runs between the two sites.
                double[][] middle = c.Payload.TryGetProperty("line", out var line) ? Line(line) : [];
                return new PlannedCable(Planned.ObjectId(c.Id), $"NY-K{c.Id}", [[a.X, a.Y], .. middle, [b.X, b.Y]]);
            }), .. parts]);
    }

    private static double[][] Line(System.Text.Json.JsonElement points) =>
        [.. points.EnumerateArray().Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() })];
}

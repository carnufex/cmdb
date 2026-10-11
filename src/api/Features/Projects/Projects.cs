using System.Globalization;
using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Plans;
using Cmdb.Graph;
using FastEndpoints;
using FluentValidation;
using Npgsql;

namespace Cmdb.Api.Features.Projects;

public sealed record ProjectSummary(long Id, string Name, string Owner, int Stages, int Applied, DateTimeOffset UpdatedAt);

/// <summary>A stage (#293): its plan, how much it changes, what is wrong with it, and the cable it lays and what it costs (#291).</summary>
/// <param name="CableMetres">Metres of new cable, from the plan's materials.</param>
/// <param name="Cost">The plan's priced materials; lines without a price are left out (<see cref="Unpriced"/>).</param>
public sealed record ProjectStage(int Seq, PlanSummary Plan, int Changes, int Problems, decimal CableMetres, decimal Cost, bool Unpriced);

/// <param name="Area">The area's outer rings in SWEREF 99 TM; none when the caller's scopes hide positions.</param>
/// <param name="HiddenStages">Stages in plans the caller's scopes do not show: counted, not shown.</param>
/// <param name="NextStep">What to do next, in words.</param>
public sealed record ProjectDetail(long Id, string Name, string Description, string Owner, string CreatedBy, double[][][]? Area, double[]? Extent,
    IReadOnlyList<ProjectStage> Stages, int HiddenStages, int Changes, decimal CableMetres, decimal Cost, string Currency, string NextStep,
    DateTimeOffset UpdatedAt);

public sealed record ProjectRequest(long Id);

public sealed class SaveProjectRequest
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Owner { get; set; }

    /// <summary>The area's outline: points in SWEREF 99 TM, closed or not.</summary>
    public double[][]? Area { get; set; }
}

public sealed class SaveProjectValidator : Validator<SaveProjectRequest>
{
    public SaveProjectValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200).When(r => r.Id == 0 || r.Name is not null);
        RuleFor(r => r.Description).MaximumLength(2000);
        RuleFor(r => r.Owner).MaximumLength(200);
        RuleFor(r => r.Area).NotNull().When(r => r.Id == 0).WithMessage("area is the project's outline.");
        RuleFor(r => r.Area).Must(a => a is { Length: >= 3 and <= 5000 } && a.All(p => p.Length == 2
                && p[0] is >= Map.TileGrid.MinX and <= Map.TileGrid.MaxX && p[1] is >= Map.TileGrid.MinY and <= Map.TileGrid.MaxY))
            .When(r => r.Area is not null).WithMessage("area is 3–5000 points [x, y] inside the map.");
    }
}

public sealed class StagesRequest
{
    public long Id { get; set; }

    /// <summary>The stages in order: plans, existing ones included.</summary>
    public long[] Plans { get; set; } = [];
}

public sealed class NewStageRequest
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>Reads and writes projects (#293), shared by the endpoints and the MCP tools.</summary>
public sealed class ProjectService(RequestDb db, GraphHolder holder, PlanViews views, Materials materials, PlanWrites writes)
{
    public async Task<List<ProjectSummary>> ListAsync(UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT p.id, p.name, p.owner,
                   (SELECT count(*) FROM project_plan s WHERE s.project_id = p.id)::int,
                   (SELECT count(*) FROM project_plan s JOIN plan l ON l.id = s.plan_id WHERE s.project_id = p.id AND l.status = 'applied')::int,
                   p.updated_at
            FROM project p WHERE {ScopeSql.Project("p.area", 1)} ORDER BY p.name
            """);
        cmd.Parameters.Add(scope.Parameter());
        var list = new List<ProjectSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ProjectSummary(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return list;
    }

    /// <summary>Null when it does not exist or the caller's scopes do not reach its area.</summary>
    public async Task<ProjectDetail?> GetAsync(long id, UserScope scope, CancellationToken ct)
    {
        string name, description, owner, createdBy;
        double[][][]? area;
        double[]? extent;
        DateTimeOffset updated;
        await using (var cmd = db.CreateCommand($"""
            SELECT p.name, p.description, p.owner, p.created_by, p.updated_at,
                   ARRAY(SELECT ARRAY[round(ST_X(q.geom)), round(ST_Y(q.geom))] FROM ST_DumpPoints(ST_ExteriorRing(g.geom)) q ORDER BY q.path) AS ring,
                   ST_XMin(p.area), ST_YMin(p.area), ST_XMax(p.area), ST_YMax(p.area), g.path[1]
            FROM project p, LATERAL ST_Dump(p.area) g
            WHERE p.id = $1 AND {ScopeSql.Project("p.area", 2)}
            ORDER BY g.path
            """))
        {
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rings = new List<double[][]>();
            (name, description, owner, createdBy, updated, extent) = ("", "", "", "", default, null);
            while (await reader.ReadAsync(ct))
            {
                (name, description, owner, createdBy, updated) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4));
                var points = (double[,])reader.GetValue(5);
                rings.Add([.. Enumerable.Range(0, points.GetLength(0)).Select(i => new[] { points[i, 0], points[i, 1] })]);
                extent = [reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9)];
            }
            if (rings.Count == 0)
            {
                return null;
            }
            area = scope.HidesCoordinates ? null : [.. rings];
            extent = scope.HidesCoordinates ? null : extent;
        }

        var plans = new List<(int Seq, long Plan)>();
        await using (var cmd = db.CreateCommand("SELECT seq, plan_id FROM project_plan WHERE project_id = $1 ORDER BY seq"))
        {
            cmd.Parameters.Add(new() { Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                plans.Add((reader.GetInt32(0), reader.GetInt64(1)));
            }
        }
        var visible = plans.Where(p => scope.SeesPlan(p.Plan)).ToList();
        var summaries = (await PlanSql.SummariesAsync(db, [.. visible.Select(p => p.Plan)], ct)).ToDictionary(s => s.Id);
        var graph = holder.Require();
        var stages = new List<ProjectStage>();
        var currency = Cmdb.Catalog.TypeCatalog.Current.Costs.Currency;
        foreach (var (seq, planId) in visible)
        {
            if (!summaries.TryGetValue(planId, out var summary) || await views.GetAsync(graph, planId, scope, ct) is not { } view)
            {
                continue;
            }
            var own = view.Chain.Operations.Where(o => o.PlanId == planId).ToList();
            // An applied or cancelled plan has nothing left to fit; only a draft's problems mean something to do.
            var problems = summary.Status == "draft" ? own.Count(o => view.Problems.ContainsKey(o.Id)) : 0;
            var bill = await materials.ForAsync(view, planId, ct);
            currency = bill.Currency;
            stages.Add(new ProjectStage(seq, summary, own.Count, problems, bill.Lines.Where(l => l.Kind == "cable").Sum(l => l.Quantity), bill.Total,
                bill.Missing.Count > 0));
        }
        return new ProjectDetail(id, name, description, owner, createdBy, area, extent, stages, plans.Count - visible.Count,
            stages.Sum(s => s.Changes), stages.Sum(s => s.CableMetres), stages.Sum(s => s.Cost), currency, NextStep(stages), updated);
    }

    /// <summary>What to do next: the first stage that is not applied, and what it needs.</summary>
    public static string NextStep(IReadOnlyList<ProjectStage> stages)
    {
        var open = stages.Where(s => s.Plan.Status == "draft").ToList();
        if (stages.Count == 0)
        {
            return "Lägg till den första etappen.";
        }
        if (open.Count == 0)
        {
            return stages.All(s => s.Plan.Status == "applied") ? "Alla etapper är införda." : "Lägg till en etapp; de andra är införda eller avbrutna.";
        }
        var next = open[0];
        return next.Changes == 0 ? $"Rita {next.Plan.Name}: etappen har inga ändringar än."
            : next.Problems > 0 ? $"Lös {next.Problems} {(next.Problems == 1 ? "ändring" : "ändringar")} som inte passar i {next.Plan.Name}."
            : next.Plan.Flag is { } flag ? $"Granska {next.Plan.Name}: {flag}"
            : $"Granska och för in {next.Plan.Name}.";
    }

    public async Task<PlanWrite<ProjectDetail>> SaveAsync(ClaimsPrincipal user, UserScope scope, SaveProjectRequest req, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        string? polygon = null;
        if (req.Area is { } area)
        {
            var ring = area.ToList();
            if (ring[0][0] != ring[^1][0] || ring[0][1] != ring[^1][1])
            {
                ring.Add(ring[0]);
            }
            polygon = "POLYGON((" + string.Join(", ", ring.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p[0]} {p[1]}"))) + "))";
            await using var check = new NpgsqlCommand("SELECT ST_IsValid(ST_GeomFromText($1, 3006)) AND ST_Area(ST_GeomFromText($1, 3006)) > 0", conn);
            check.Parameters.Add(new() { Value = polygon });
            if (await check.ExecuteScalarAsync(ct) is not true)
            {
                return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.Invalid, "Området korsar sig självt eller saknar yta.");
            }
        }
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Operations.Operations.BeginAsync(conn, tx, user, req.Id == 0 ? "create_project" : "update_project",
            new { project = req.Id, name = req.Name, owner = req.Owner, area = polygon is not null }, req.Description, ct);
        long id;
        try
        {
            if (req.Id == 0)
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO project (name, description, owner, created_by, area) VALUES ($1, $2, $3, $4, ST_GeomFromText($5, 3006)) RETURNING id
                    """, conn, tx);
                cmd.Parameters.Add(new() { Value = req.Name!.Trim() });
                cmd.Parameters.Add(new() { Value = req.Description?.Trim() ?? "" });
                cmd.Parameters.Add(new() { Value = string.IsNullOrWhiteSpace(req.Owner) ? PlanSql.Actor(user) : req.Owner.Trim() });
                cmd.Parameters.Add(new() { Value = PlanSql.Actor(user) });
                cmd.Parameters.Add(new() { Value = polygon! });
                id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            else
            {
                await using var cmd = new NpgsqlCommand("""
                    UPDATE project SET name = coalesce($2, name), description = coalesce($3, description), owner = coalesce($4, owner),
                           area = coalesce(ST_GeomFromText($5, 3006), area), updated_at = now()
                    WHERE id = $1 RETURNING id
                    """, conn, tx);
                cmd.Parameters.Add(new() { Value = req.Id });
                cmd.Parameters.Add(new() { Value = (object?)req.Name?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                cmd.Parameters.Add(new() { Value = (object?)req.Description?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                cmd.Parameters.Add(new() { Value = (object?)req.Owner?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                cmd.Parameters.Add(new() { Value = (object?)polygon ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                if (await cmd.ExecuteScalarAsync(ct) is not long updated)
                {
                    return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.NotFound, $"Projekt {req.Id} finns inte.");
                }
                id = updated;
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.Conflict, $"Det finns redan ett projekt som heter {req.Name}.");
        }
        await tx.CommitAsync(ct);
        return new((await GetAsync(id, scope, ct))!);
    }

    /// <summary>The stages in a new order, or other existing plans as stages. A plan in another project cannot be one.</summary>
    public async Task<PlanWrite<ProjectDetail>> SetStagesAsync(ClaimsPrincipal user, UserScope scope, StagesRequest req, CancellationToken ct)
    {
        if (await GetAsync(req.Id, scope, ct) is null)
        {
            return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.NotFound, $"Projekt {req.Id} finns inte.");
        }
        var plans = req.Plans.Distinct().ToArray();
        if (plans.Length != req.Plans.Length || plans.Length > 200)
        {
            return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.Invalid, "Ge varje plan en gång, högst 200.");
        }
        var found = await PlanSql.SummariesAsync(db, plans, ct);
        if (found.Count != plans.Length || plans.Any(p => !scope.SeesPlan(p)))
        {
            return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.Invalid, "En av planerna finns inte.");
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Operations.Operations.BeginAsync(conn, tx, user, "set_project_stages", new { project = req.Id, plans }, null, ct);
        await using (var other = new NpgsqlCommand("SELECT p.name FROM project_plan s JOIN project p ON p.id = s.project_id WHERE s.plan_id = ANY($1) AND s.project_id <> $2 LIMIT 1", conn, tx))
        {
            other.Parameters.Add(new() { Value = plans });
            other.Parameters.Add(new() { Value = req.Id });
            if (await other.ExecuteScalarAsync(ct) is string project)
            {
                return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.Conflict, $"En av planerna är redan en etapp i {project}.");
            }
        }
        await using (var clear = new NpgsqlCommand("DELETE FROM project_plan WHERE project_id = $1", conn, tx))
        {
            clear.Parameters.Add(new() { Value = req.Id });
            await clear.ExecuteNonQueryAsync(ct);
        }
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO project_plan (project_id, plan_id, seq) SELECT $1, u.plan, u.n FROM unnest($2::bigint[]) WITH ORDINALITY AS u(plan, n)", conn, tx))
        {
            insert.Parameters.Add(new() { Value = req.Id });
            insert.Parameters.Add(new() { Value = plans });
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var touch = new NpgsqlCommand("UPDATE project SET updated_at = now() WHERE id = $1", conn, tx))
        {
            touch.Parameters.Add(new() { Value = req.Id });
            await touch.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new((await GetAsync(req.Id, scope, ct))!);
    }

    /// <summary>A new stage last: a draft plan that builds on the stage before it.</summary>
    public async Task<PlanWrite<ProjectDetail>> AddStageAsync(ClaimsPrincipal user, string via, UserScope scope, NewStageRequest req, CancellationToken ct)
    {
        if (await GetAsync(req.Id, scope, ct) is not { } project)
        {
            return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.NotFound, $"Projekt {req.Id} finns inte.");
        }
        if (project.HiddenStages > 0)
        {
            return PlanWrite.Fail<ProjectDetail>(PlanWriteFailure.Forbidden, "Projektet har etapper utanför ditt omfång.");
        }
        var previous = project.Stages.LastOrDefault(s => s.Plan.Status != "cancelled");
        var name = string.IsNullOrWhiteSpace(req.Name) ? $"{project.Name}, etapp {project.Stages.Count + 1}" : req.Name.Trim();
        var plan = await writes.CreateAsync(user, via, scope, name, $"Etapp {project.Stages.Count + 1} i projektet {project.Name}.",
            previous is null ? [] : [previous.Plan.Id], ct);
        if (plan.Value is null)
        {
            return PlanWrite.Fail<ProjectDetail>(plan.Failure, plan.Error!);
        }
        return await SetStagesAsync(user, scope, new StagesRequest { Id = req.Id, Plans = [.. project.Stages.Select(s => s.Plan.Id), plan.Value.Id] }, ct);
    }
}

internal static class ProjectResults
{
    public static async Task SendAsync<TRequest>(Endpoint<TRequest, ProjectDetail> endpoint, PlanWrite<ProjectDetail> result, CancellationToken ct)
        where TRequest : notnull
    {
        switch (result.Failure)
        {
            case PlanWriteFailure.None:
                await endpoint.HttpContext.Response.SendOkAsync(result.Value!, cancellation: ct);
                return;
            case PlanWriteFailure.NotFound:
                await endpoint.HttpContext.Response.SendNotFoundAsync(ct);
                return;
            case PlanWriteFailure.Conflict:
                await PlanSql.ConflictAsync(endpoint.HttpContext, result.Error!, ct);
                return;
            case PlanWriteFailure.Forbidden:
                await TypedResults.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(endpoint.HttpContext);
                return;
            default:
                await TypedResults.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(endpoint.HttpContext);
                return;
        }
    }
}

/// <summary>The projects whose area the caller's scopes reach (#293).</summary>
public sealed class ListProjectsEndpoint(ProjectService projects) : EndpointWithoutRequest<List<ProjectSummary>>
{
    public override void Configure() => Get("/projects");

    public override async Task HandleAsync(CancellationToken ct) => await Send.OkAsync(await projects.ListAsync(HttpContext.Scope(), ct), ct);
}

/// <summary>A project with its area, its stages and their changes, cable and cost, and what to do next.</summary>
public sealed class GetProjectEndpoint(ProjectService projects) : Endpoint<ProjectRequest, ProjectDetail>
{
    public override void Configure() => Get("/projects/{id}");

    public override async Task HandleAsync(ProjectRequest req, CancellationToken ct)
    {
        if (await projects.GetAsync(req.Id, HttpContext.Scope(), ct) is not { } project)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(project, ct);
    }
}

public sealed class CreateProjectEndpoint(ProjectService projects) : Endpoint<SaveProjectRequest, ProjectDetail>
{
    public override void Configure()
    {
        Post("/projects");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(SaveProjectRequest req, CancellationToken ct)
    {
        req.Id = 0;
        await ProjectResults.SendAsync(this, await projects.SaveAsync(User, HttpContext.Scope(), req, ct), ct);
    }
}

public sealed class UpdateProjectEndpoint(ProjectService projects) : Endpoint<SaveProjectRequest, ProjectDetail>
{
    public override void Configure()
    {
        Put("/projects/{id}");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(SaveProjectRequest req, CancellationToken ct) =>
        await ProjectResults.SendAsync(this, await projects.SaveAsync(User, HttpContext.Scope(), req, ct), ct);
}

public sealed class SetStagesEndpoint(ProjectService projects) : Endpoint<StagesRequest, ProjectDetail>
{
    public override void Configure()
    {
        Put("/projects/{id}/stages");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(StagesRequest req, CancellationToken ct) =>
        await ProjectResults.SendAsync(this, await projects.SetStagesAsync(User, HttpContext.Scope(), req, ct), ct);
}

public sealed class AddStageEndpoint(ProjectService projects) : Endpoint<NewStageRequest, ProjectDetail>
{
    public override void Configure()
    {
        Post("/projects/{id}/stages");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(NewStageRequest req, CancellationToken ct) =>
        await ProjectResults.SendAsync(this, await projects.AddStageAsync(User, PlanWrites.Via(HttpContext), HttpContext.Scope(), req, ct), ct);
}

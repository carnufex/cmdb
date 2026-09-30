using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>The outcome of a write: the result, or an error and whether it was a missing plan, bad input or a conflict.</summary>
public sealed record PlanWrite<T>(T? Value, string? Error = null, PlanWriteFailure Failure = PlanWriteFailure.None)
    where T : class;

internal static class PlanWrite
{
    public static PlanWrite<T> Fail<T>(PlanWriteFailure failure, string error)
        where T : class => new(null, error, failure);
}

public enum PlanWriteFailure
{
    None,
    NotFound,
    Invalid,
    Conflict,
}

/// <summary>
/// Creating plans and adding operations (#24), shared by the REST endpoints and the MCP tools (#64), so a person and
/// an agent go through the same checks: the plan must be a visible draft, and terminals and objects must be inside the
/// caller's scopes.
/// </summary>
public sealed class PlanWrites(RequestDb db, GraphHolder holder, PlanViews views, ScopeMasks masks)
{
    /// <summary>How the caller reaches us: mcp for anything on /mcp, otherwise api.</summary>
    public static string Via(HttpContext http) =>
        http.Request.Path.StartsWithSegments(Agents.McpSetup.Path, StringComparison.Ordinal) ? "mcp" : "api";

    public async Task<PlanWrite<PlanSummary>> CreateAsync(ClaimsPrincipal user, string via, UserScope scope, string name, string? description,
        long[] dependsOn, CancellationToken ct)
    {
        dependsOn = [.. dependsOn.Distinct()];
        if (await PlanSql.CheckDependenciesAsync(db, dependsOn, scope, ct) is { } problem)
        {
            return PlanWrite.Fail<PlanSummary>(PlanWriteFailure.Invalid, problem);
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO plan (name, description, created_by, created_via, client) VALUES ($1, $2, $3, $4, $5) RETURNING id
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = name });
            cmd.Parameters.Add(new() { Value = description ?? "" });
            cmd.Parameters.Add(new() { Value = PlanSql.Actor(user) });
            cmd.Parameters.Add(new() { Value = via });
            cmd.Parameters.Add(new() { Value = (object?)user.FindFirstValue(CmdbClaims.Client) ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await PlanSql.InsertDependenciesAsync(conn, tx, id, dependsOn, ct);
        await tx.CommitAsync(ct);
        return new((await PlanSql.SummariesAsync(db, [id], ct)).Single());
    }

    public async Task<PlanWrite<PlanOperationView>> AddAsync(ClaimsPrincipal user, UserScope scope, AddOperationRequest req, CancellationToken ct)
    {
        var errors = new AddOperationValidator().Validate(req).Errors;
        if (errors.Count > 0)
        {
            return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, string.Join(" ", errors.Select(e => e.ErrorMessage)));
        }
        var graph = holder.Require();
        if (await views.GetAsync(graph, req.Id, scope, ct) is not { } view)
        {
            return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.NotFound, $"Plan {req.Id} finns inte.");
        }
        if (view.Chain.Plan.Status != "draft")
        {
            return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Conflict, "Bara utkast kan ändras.");
        }
        var mask = await masks.GetAsync(graph, scope, ct);
        string payload;
        if (req.Kind is "create_site" or "create_equipment" or "create_cable")
        {
            var (created, error) = await CreationAsync(scope, view, req, ct);
            if (created is null)
            {
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, error!);
            }
            payload = created;
        }
        else if (req.Kind is "connect" or "disconnect")
        {
            // Terminals must exist in the plan's view (production or planned) and be inside the caller's scopes; outside,
            // they do not exist.
            foreach (var terminal in new[] { req.A!.Value, req.B!.Value })
            {
                if (!view.Graph.TryGetNode(terminal, out var node) || !Visible(view.Graph, mask, node))
                {
                    return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, $"Terminal {terminal} finns inte.");
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
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, $"{req.Type} {req.ObjectId} finns inte.");
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
            cmd.Parameters.Add(new() { Value = PlanSql.Actor(user) });
            opId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await PlanSql.TouchAsync(conn, tx, req.Id, ct);
        await tx.CommitAsync(ct);

        var after = (await views.GetAsync(graph, req.Id, scope, ct))!;
        var op = after.Chain.Operations.Single(o => o.Id == opId);
        return new((await PlanSql.DescribeAsync(db, graph, mask, scope, [op], after.Problems, ct, after.Chain.Operations)).Single());
    }

    /// <summary>
    /// Checks a create operation (#107) and gives its payload: sites it refers to must exist and be visible, or be
    /// planned in the plan's view; a planned site must lie inside the caller's scopes; codes must be new.
    /// </summary>
    private async Task<(string? Payload, string? Error)> CreationAsync(UserScope scope, PlanView view, AddOperationRequest req, CancellationToken ct)
    {
        async Task<string?> SiteProblem(long site)
        {
            if (site < 0)
            {
                return view.Chain.Operations.Any(o => o.Kind == "create_site" && Planned.ObjectId(o.Id) == site)
                    ? null : $"Den planerade siten {site} finns inte i planen.";
            }
            return await PlanSql.ObjectVisibleAsync(db, "site", site, scope, ct) ? null : $"Site {site} finns inte.";
        }

        switch (req.Kind)
        {
            case "create_site":
                {
                    var code = req.Code!.Trim();
                    if (view.Chain.Operations.Any(o => o.Kind == "create_site" && o.Payload.GetProperty("code").GetString() == code))
                    {
                        return (null, $"Koden {code} används redan i planen.");
                    }
                    await using (var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM site WHERE code = $1)"))
                    {
                        cmd.Parameters.Add(new() { Value = code });
                        if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
                        {
                            return (null, $"Koden {code} används redan.");
                        }
                    }
                    if (!await InsideScopeAsync(scope, req.X!.Value, req.Y!.Value, req.SiteType!, ct))
                    {
                        return (null, "Positionen ligger utanför ditt omfång.");
                    }
                    return (System.Text.Json.JsonSerializer.Serialize(new
                    {
                        code,
                        name = req.Name!.Trim(),
                        siteType = req.SiteType,
                        x = Math.Round(req.X!.Value, 1),
                        y = Math.Round(req.Y!.Value, 1),
                    }), null);
                }
            case "create_equipment":
                return await SiteProblem(req.SiteId!.Value) is { } equipmentSite
                    ? (null, equipmentSite)
                    : (System.Text.Json.JsonSerializer.Serialize(new
                    {
                        site = req.SiteId,
                        typeKey = req.TypeKey,
                        name = req.Name!.Trim(),
                        rack = string.IsNullOrWhiteSpace(req.Rack) ? null : req.Rack.Trim(),
                    }), null);
            default:
                foreach (var site in new[] { req.ASiteId!.Value, req.BSiteId!.Value })
                {
                    if (await SiteProblem(site) is { } cableSite)
                    {
                        return (null, cableSite);
                    }
                }
                return (System.Text.Json.JsonSerializer.Serialize(new { a = req.ASiteId, b = req.BSiteId, typeKey = req.TypeKey }), null);
        }
    }

    /// <summary>Whether a new site of the type at the point is inside one of the caller's scopes.</summary>
    private async Task<bool> InsideScopeAsync(UserScope scope, double x, double y, string siteType, CancellationToken ct)
    {
        if (scope.Unrestricted)
        {
            return true;
        }
        await using var cmd = db.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM access_scope a
                WHERE a.key = ANY($1)
                  AND (a.area IS NULL OR ST_Intersects(a.area, ST_SetSRID(ST_MakePoint($2, $3), 3006)))
                  AND (cardinality(a.site_types) = 0 OR $4 = ANY(a.site_types)))
            """);
        cmd.Parameters.Add(new() { Value = scope.Keys });
        cmd.Parameters.Add(new() { Value = x });
        cmd.Parameters.Add(new() { Value = y });
        cmd.Parameters.Add(new() { Value = siteType });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static bool Visible(Cmdb.Graph.Graph g, GraphMask mask, int node)
    {
        var site = g.SiteIndexOfNode(node);
        return mask.NodeVisible(g, node);
    }
}

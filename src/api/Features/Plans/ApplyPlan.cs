using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Reservations;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

public sealed record ApplyResult(PlanSummary Plan, IReadOnlyList<PlanSummary> Flagged);

/// <summary>
/// Brings a plan into production (#24, ADR-0005 rule 3): its operations run in one transaction, the change stream (#11)
/// carries them into the graph, and draft plans building on it are checked against the new production. Those whose
/// operations no longer fit are flagged. Every dependency must be in production first, and every operation must fit.
/// </summary>
public sealed partial class ApplyPlanEndpoint(RequestDb db, GraphHolder holder, PlanViews views, TrustedApplications trust,
    ILogger<ApplyPlanEndpoint> logger)
    : Endpoint<PlanIdRequest, ApplyResult>
{
    public override void Configure()
    {
        Post("/plans/{id}/apply");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(PlanIdRequest req, CancellationToken ct)
    {
        // Agents propose, people decide (#64, ADR-0011): no agent client may bring a plan into production.
        if (User.FindFirstValue(CmdbClaims.Client) is { } client && trust.AgentClients.Contains(client))
        {
            await Send.ResultAsync(TypedResults.Problem("Agenter kan inte föra in planer. En människa granskar och för in dem.",
                statusCode: StatusCodes.Status403Forbidden));
            return;
        }
        var scope = HttpContext.Scope();
        var graph = holder.Require();
        if (await views.GetAsync(graph, req.Id, scope, ct) is not { } view)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var plan = view.Chain.Plan;
        if (plan.Status != "draft")
        {
            await PlanSql.ConflictAsync(HttpContext, "Planen är redan införd eller avbruten.", ct);
            return;
        }
        var pending = view.Chain.Plans.Where(p => p.Id != plan.Id).Select(p => p.Name).ToList();
        if (pending.Count > 0)
        {
            await PlanSql.ConflictAsync(HttpContext, $"Beroendena måste föras in först: {string.Join(", ", pending)}.", ct);
            return;
        }
        if (view.Problems.Count > 0)
        {
            await PlanSql.ConflictAsync(HttpContext, $"{view.Problems.Count} operationer passar inte produktion. Åtgärda dem först.", ct);
            return;
        }
        var blocked = (await ClaimsSql.ConflictsAsync(db, [plan.Id], ct)).Where(c => c.Blocking).ToList();
        if (blocked.Count > 0)
        {
            await PlanSql.ConflictAsync(HttpContext,
                $"{blocked.Select(c => c.OperationId).Distinct().Count()} operationer använder resurser som andra har reserverat. {blocked[0].Message(scope)}", ct);
            return;
        }

        var operations = view.Chain.Operations;
        PlanApply run;
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            // Serialises concurrent applies of the same plan and re-checks its status under the lock.
            await using (var cmd = new NpgsqlCommand("SELECT status, version FROM plan WHERE id = $1 FOR UPDATE", conn, tx))
            {
                cmd.Parameters.Add(new() { Value = plan.Id });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                if (reader.GetString(0) != "draft" || reader.GetInt32(1) != plan.Version)
                {
                    await reader.DisposeAsync();
                    await PlanSql.ConflictAsync(HttpContext, "Planen ändrades under tiden. Försök igen.", ct);
                    return;
                }
            }
            run = new PlanApply(conn, tx);
            foreach (var op in operations)
            {
                if (!await run.RunAsync(op, ct))
                {
                    await tx.RollbackAsync(ct);
                    await PlanSql.ConflictAsync(HttpContext, $"Operation {op.Seq} passar inte längre produktion. Inget har ändrats.", ct);
                    return;
                }
            }
            await using (var cmd = new NpgsqlCommand("""
                UPDATE plan SET status = 'applied', applied_by = $2, applied_at = now(), version = version + 1, updated_at = now(), flag = NULL
                WHERE id = $1
                """, conn, tx))
            {
                cmd.Parameters.Add(new() { Value = plan.Id });
                cmd.Parameters.Add(new() { Value = PlanSql.Actor(User) });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            // In production the resources are taken by the connections themselves (#25).
            await PlanSql.ReleaseReservationsAsync(conn, tx, plan.Id, ct);
            // Plans building on this one may refer to objects it created (#107): they now have production ids.
            await run.RewriteDependentsAsync(await PlanSql.DependentsAsync(db, plan.Id, ct), ct);
            await tx.CommitAsync(ct);
        }
        var actor = PlanSql.Actor(User);
        Applied(logger, plan.Id, operations.Count, actor);

        // Production as it will be once the change stream has caught up: the graph plus this plan's connections.
        var (after, _) = graph.WithChanges([.. operations.Where(o => o.Change is not null).Select(o => run.Production(o.Change!))]);
        var flagged = await FlagDependentsAsync(db, views, after, plan, ct);
        await Send.OkAsync(new ApplyResult((await PlanSql.SummariesAsync(db, [plan.Id], ct)).Single(), flagged), ct);
    }

    /// <summary>Re-checks every draft plan that builds on the applied one, directly or not, and flags those that no longer fit.</summary>
    private static async Task<List<PlanSummary>> FlagDependentsAsync(NpgsqlDataSource db, PlanViews views, Cmdb.Graph.Graph after,
        PlanRow applied, CancellationToken ct)
    {
        var flagged = new List<PlanSummary>();
        foreach (var id in await PlanSql.DependentsAsync(db, applied.Id, ct))
        {
            if (await PlanViews.LoadChainAsync(db, id, ct) is not { } chain || chain.Plan.Status != "draft")
            {
                continue;
            }
            var view = views.View(after, chain);
            var own = chain.Operations.Where(o => o.PlanId == id && view.Problems.ContainsKey(o.Id)).ToList();
            if (own.Count == 0)
            {
                continue;
            }
            await PlanSql.FlagAsync(db, id, $"Efter att {applied.Name} fördes in passar {own.Count} operationer inte längre.", ct);
            flagged.Add((await PlanSql.SummariesAsync(db, [id], ct)).Single());
        }
        return flagged;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plan {Plan} applied: {Operations} operation(s) by {Actor}")]
    private static partial void Applied(ILogger logger, long plan, int operations, string actor);
}

/// <summary>Cancels a draft plan (ADR-0005 rule 4): every draft plan building on it, directly or not, is flagged.</summary>
public sealed class CancelPlanEndpoint(RequestDb db) : Endpoint<PlanIdRequest, ApplyResult>
{
    public override void Configure()
    {
        Post("/plans/{id}/cancel");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(PlanIdRequest req, CancellationToken ct)
    {
        if (!HttpContext.Scope().SeesPlan(req.Id) || (await PlanSql.SummariesAsync(db, [req.Id], ct)).SingleOrDefault() is not { } plan)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (plan.Status != "draft")
        {
            await PlanSql.ConflictAsync(HttpContext, "Planen är redan införd eller avbruten.", ct);
            return;
        }
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using (var cmd = new NpgsqlCommand("""
                UPDATE plan SET status = 'cancelled', cancelled_at = now(), version = version + 1, updated_at = now() WHERE id = $1 AND status = 'draft'
                """, conn, tx))
            {
                cmd.Parameters.Add(new() { Value = req.Id });
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await PlanSql.ReleaseReservationsAsync(conn, tx, req.Id, ct);
            await tx.CommitAsync(ct);
        }
        var flagged = new List<PlanSummary>();
        foreach (var id in await PlanSql.DependentsAsync(db, req.Id, ct))
        {
            await PlanSql.FlagAsync(db, id, $"Beroendet {plan.Name} avbröts.", ct);
            flagged.Add((await PlanSql.SummariesAsync(db, [id], ct)).Single());
        }
        await Send.OkAsync(new ApplyResult((await PlanSql.SummariesAsync(db, [req.Id], ct)).Single(), flagged), ct);
    }
}

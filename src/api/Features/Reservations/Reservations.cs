using Cmdb.Api.Auth;
using Cmdb.Api.Features.Plans;
using Cmdb.Api.Features.Trace;
using Cmdb.Graph;
using FastEndpoints;
using FluentValidation;
using Npgsql;

namespace Cmdb.Api.Features.Reservations;

/// <param name="Label">The resource in words: "HUB-001 ODF-96 1 · 3", "K-000123 ledare 7", "SW-1 slot 2".</param>
public sealed record ReservationView(long Id, string ResourceKind, long ResourceId, string? Slot, string Label, string HolderKind,
    long HolderId, string Holder, string Reason, string CreatedBy, DateTimeOffset CreatedAt);

public sealed class ListReservationsRequest
{
    [QueryParam]
    public long? Plan { get; set; }

    [QueryParam]
    public long? Service { get; set; }
}

public sealed class CreateReservationRequest
{
    public string ResourceKind { get; set; } = "";
    public long ResourceId { get; set; }
    public string? Slot { get; set; }
    public long? PlanId { get; set; }
    public long? ServiceId { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class CreateReservationValidator : Validator<CreateReservationRequest>
{
    public CreateReservationValidator()
    {
        RuleFor(r => r.ResourceKind).Must(k => k is "terminal" or "conductor" or "slot" or "channel")
            .WithMessage("resourceKind is terminal, conductor, slot or channel.");
        RuleFor(r => r.Slot).NotEmpty().MaximumLength(50).When(r => r.ResourceKind == "slot");
        RuleFor(r => r.Slot).Null().When(r => r.ResourceKind != "slot").WithMessage("slot is only for slots.");
        RuleFor(r => r).Must(r => r.PlanId.HasValue != r.ServiceId.HasValue).WithName("holder").WithMessage("Give exactly one of planId or serviceId.");
        RuleFor(r => r.Reason).MaximumLength(1000);
    }
}

public sealed class ReservationIdRequest
{
    public long Id { get; set; }
}

/// <summary>A plan's or a service's active reservations (#25).</summary>
public sealed class ListReservationsEndpoint(RequestDb db) : Endpoint<ListReservationsRequest, List<ReservationView>>
{
    public override void Configure() => Get("/reservations");

    public override async Task HandleAsync(ListReservationsRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (req.Plan.HasValue == req.Service.HasValue)
        {
            AddError("Give exactly one of plan or service.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var (kind, id) = req.Plan is { } plan ? ("plan", plan) : ("service", req.Service!.Value);
        if (!await ReservationSql.HolderVisibleAsync(db, kind, id, scope, draftOnly: false, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(await ReservationSql.ListAsync(db, kind, id, null, ct), ct);
    }
}

/// <summary>
/// Reserves a resource for a plan or a service (#25). One active holder per resource: a second reservation is a
/// conflict naming the holder, when the caller may see it. Only resources inside the caller's scopes can be reserved.
/// </summary>
public sealed class CreateReservationEndpoint(RequestDb db, GraphHolder holder, ScopeMasks masks)
    : Endpoint<CreateReservationRequest, ReservationView>
{
    public override void Configure()
    {
        Post("/reservations");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(CreateReservationRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var (holderKind, holderId) = req.PlanId is { } plan ? ("plan", plan) : ("service", req.ServiceId!.Value);
        if (!await ReservationSql.HolderVisibleAsync(db, holderKind, holderId, scope, draftOnly: true, ct))
        {
            AddError(holderKind == "plan" ? $"Planen {holderId} finns inte eller är inte ett utkast." : $"Tjänsten {holderId} finns inte.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var graph = holder.Require();
        var mask = await masks.GetAsync(graph, scope, ct);
        if (!await ReservationSql.ResourceVisibleAsync(db, graph, mask, scope, req.ResourceKind, req.ResourceId, req.Slot, ct))
        {
            AddError($"{req.ResourceKind} {req.ResourceId} finns inte.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        long id;
        try
        {
            await using var cmd = db.CreateCommand("""
                INSERT INTO reservation (resource_kind, resource_id, slot, holder_kind, holder_id, reason, created_by)
                VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
                """);
            cmd.Parameters.Add(new() { Value = req.ResourceKind });
            cmd.Parameters.Add(new() { Value = req.ResourceId });
            cmd.Parameters.Add(new() { Value = (object?)req.Slot ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = holderKind });
            cmd.Parameters.Add(new() { Value = holderId });
            cmd.Parameters.Add(new() { Value = req.Reason });
            cmd.Parameters.Add(new() { Value = PlanSql.Actor(User) });
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            var existing = await ReservationSql.ActiveOnAsync(db, req.ResourceKind, req.ResourceId, req.Slot, ct);
            var who = existing is null ? "någon annan"
                : existing.HolderKind == "plan" ? (scope.SeesPlan(existing.HolderId) ? $"planen {existing.Holder}" : "en annan plan")
                : "en tjänst";
            await PlanSql.ConflictAsync(HttpContext, $"Resursen är redan reserverad av {who}.", ct);
            return;
        }
        if (holderKind == "plan")
        {
            // Conflicts are part of the plan's view.
            await ReservationSql.TouchPlanAsync(db, holderId, ct);
        }
        await Send.OkAsync((await ReservationSql.ListAsync(db, holderKind, holderId, id, ct)).Single(), ct);
    }
}

/// <summary>Releases a reservation.</summary>
public sealed class DeleteReservationEndpoint(RequestDb db) : Endpoint<ReservationIdRequest>
{
    public override void Configure()
    {
        Delete("/reservations/{id}");
        Roles("cmdb-full");
    }

    public override async Task HandleAsync(ReservationIdRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        await using (var cmd = db.CreateCommand("SELECT holder_kind, holder_id FROM reservation WHERE id = $1 AND released_at IS NULL"))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)
                || !await ReservationSql.HolderVisibleAsync(db, reader.GetString(0), reader.GetInt64(1), scope, draftOnly: false, ct))
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            var (kind, holderId) = (reader.GetString(0), reader.GetInt64(1));
            await reader.DisposeAsync();
            await using var release = db.CreateCommand("UPDATE reservation SET released_at = now() WHERE id = $1");
            release.Parameters.Add(new() { Value = req.Id });
            await release.ExecuteNonQueryAsync(ct);
            if (kind == "plan")
            {
                await ReservationSql.TouchPlanAsync(db, holderId, ct);
            }
        }
        await Send.NoContentAsync(ct);
    }
}

internal static class ReservationSql
{
    public static async Task<bool> HolderVisibleAsync(NpgsqlDataSource db, string kind, long id, UserScope scope, bool draftOnly, CancellationToken ct)
    {
        if (kind == "plan" && !scope.SeesPlan(id))
        {
            return false;
        }
        var sql = kind == "plan"
            ? $"SELECT EXISTS (SELECT 1 FROM plan WHERE id = $1 {(draftOnly ? "AND status = 'draft'" : "")})"
            : $"SELECT EXISTS (SELECT 1 FROM service s WHERE s.id = $1 AND {ScopeSql.Service("s.id", 2)})";
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = id });
        if (kind != "plan")
        {
            cmd.Parameters.Add(scope.Parameter());
        }
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<bool> ResourceVisibleAsync(NpgsqlDataSource db, Cmdb.Graph.Graph graph, GraphMask mask, UserScope scope,
        string kind, long id, string? slot, CancellationToken ct)
    {
        switch (kind)
        {
            case "terminal":
                return graph.TryGetNode(id, out var node) && NodeVisible(graph, mask, node);
            case "channel":
                await using (var cmd = db.CreateCommand("SELECT terminal_id FROM channel WHERE id = $1"))
                {
                    cmd.Parameters.Add(new() { Value = id });
                    return await cmd.ExecuteScalarAsync(ct) is long terminal && graph.TryGetNode(terminal, out var n) && NodeVisible(graph, mask, n);
                }
            case "conductor":
                await using (var cmd = db.CreateCommand($"SELECT EXISTS (SELECT 1 FROM conductor k WHERE k.id = $1 AND {ScopeSql.Cable("k.cable_id", 2)})"))
                {
                    cmd.Parameters.Add(new() { Value = id });
                    cmd.Parameters.Add(scope.Parameter());
                    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
                }
            default:
                // A slot on equipment: the equipment's type must have it.
                await using (var cmd = db.CreateCommand($"""
                    SELECT EXISTS (
                        SELECT 1 FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id
                        WHERE e.id = $1 AND {ScopeSql.Site("e.site_id", 2)}
                          AND EXISTS (SELECT 1 FROM jsonb_array_elements(t.slot_template) s WHERE s->>'name' = $3))
                    """))
                {
                    cmd.Parameters.Add(new() { Value = id });
                    cmd.Parameters.Add(scope.Parameter());
                    cmd.Parameters.Add(new() { Value = slot! });
                    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
                }
        }
    }

    public static async Task<ReservationView?> ActiveOnAsync(NpgsqlDataSource db, string kind, long id, string? slot, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT holder_kind, holder_id FROM reservation
            WHERE released_at IS NULL AND resource_kind = $1 AND resource_id = $2 AND slot IS NOT DISTINCT FROM $3
            """);
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = (object?)slot ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var (holderKind, holderId) = (reader.GetString(0), reader.GetInt64(1));
        await reader.DisposeAsync();
        return (await ListAsync(db, holderKind, holderId, null, ct)).FirstOrDefault(r => r.ResourceKind == kind && r.ResourceId == id);
    }

    /// <summary>A holder's active reservations with their resources in words; one of them when <paramref name="only"/> is set.</summary>
    public static async Task<List<ReservationView>> ListAsync(NpgsqlDataSource db, string holderKind, long holderId, long? only, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT r.id, r.resource_kind, r.resource_id, r.slot, r.holder_kind, r.holder_id,
                   CASE r.holder_kind WHEN 'plan' THEN (SELECT name FROM plan WHERE id = r.holder_id)
                                      ELSE (SELECT code FROM service WHERE id = r.holder_id) END,
                   r.reason, r.created_by, r.created_at,
                   CASE r.resource_kind
                       WHEN 'conductor' THEN (SELECT c.code || ' ledare ' || k.number FROM conductor k JOIN cable c ON c.id = k.cable_id WHERE k.id = r.resource_id)
                       WHEN 'slot' THEN (SELECT e.name || ' slot ' || r.slot FROM equipment e WHERE e.id = r.resource_id)
                       WHEN 'channel' THEN (SELECT ch.kind || ' ' || ch.number FROM channel ch WHERE ch.id = r.resource_id)
                   END,
                   CASE r.resource_kind WHEN 'terminal' THEN r.resource_id
                                        WHEN 'channel' THEN (SELECT terminal_id FROM channel WHERE id = r.resource_id) END
            FROM reservation r
            WHERE r.released_at IS NULL AND r.holder_kind = $1 AND r.holder_id = $2 AND ($3::bigint IS NULL OR r.id = $3)
            ORDER BY r.id
            """);
        cmd.Parameters.Add(new() { Value = holderKind });
        cmd.Parameters.Add(new() { Value = holderId });
        cmd.Parameters.Add(new() { Value = (object?)only ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        var rows = new List<(ReservationView View, long? Terminal)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((new ReservationView(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(10) ? "" : reader.GetString(10), reader.GetString(4), reader.GetInt64(5), reader.IsDBNull(6) ? "?" : reader.GetString(6),
                    reader.GetString(7), reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(9)), reader.IsDBNull(11) ? null : reader.GetInt64(11)));
            }
        }
        var terminals = rows.Where(r => r.Terminal.HasValue).Select(r => r.Terminal!.Value).Distinct().ToArray();
        if (terminals.Length == 0)
        {
            return [.. rows.Select(r => r.View)];
        }
        var names = await TraceNames.LoadAsync(db, terminals, [], [], ct);
        return [.. rows.Select(r => r.Terminal is { } t
            ? r.View with { Label = r.View.ResourceKind == "channel" ? $"{names.Hop(t, null).Label} {r.View.Label}" : names.Hop(t, null).Label }
            : r.View)];
    }

    public static async Task TouchPlanAsync(NpgsqlDataSource db, long planId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("UPDATE plan SET version = version + 1, updated_at = now() WHERE id = $1");
        cmd.Parameters.Add(new() { Value = planId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool NodeVisible(Cmdb.Graph.Graph g, GraphMask mask, int node)
    {
        var site = g.SiteIndexOfNode(node);
        return mask.NodeVisible(g, node);
    }
}

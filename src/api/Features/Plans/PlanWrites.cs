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
        else if (req.Kind == "set_classification")
        {
            // The same checks as setting it directly: the object is in the caller's scopes, the schema and level exist and fit (#176).
            // A negative id is a site or equipment this plan (or one under it) creates (#179).
            var target = req.ObjectId!.Value;
            var exists = target < 0
                ? req.Type is "site" or "equipment" && view.Chain.Operations.Any(o => o.Kind == $"create_{req.Type}" && Planned.ObjectId(o.Id) == target)
                : await PlanSql.ObjectVisibleAsync(db, req.Type!, target, scope, ct);
            if (!exists)
            {
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, $"{req.Type} {req.ObjectId} finns inte.");
            }
            if (Classifications.ClassificationStore.Problem(req.Type!, req.Schema!, req.Level) is { } classificationProblem)
            {
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, classificationProblem);
            }
            payload = System.Text.Json.JsonSerializer.Serialize(new { type = req.Type, id = req.ObjectId, schema = req.Schema, level = req.Level });
        }
        else if (req.Kind == "remove")
        {
            var (removal, error) = await RemovalAsync(scope, view, req, ct);
            if (removal is null)
            {
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, error!);
            }
            payload = removal;
        }
        else if (req.Kind == "set_conductor_usage")
        {
            var (usage, error) = await ConductorUsageAsync(scope, req, ct);
            if (usage is null)
            {
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, error!);
            }
            payload = usage;
        }
        else if (req.Kind == "move")
        {
            var (move, error) = await MoveAsync(scope, view, req, ct);
            if (move is null)
            {
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, error!);
            }
            payload = move;
        }
        else if (req.Kind == "split_cable")
        {
            var (split, error) = await SplitAsync(scope, view, req, ct);
            if (split is null)
            {
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, error!);
            }
            payload = split;
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
            if (req.Kind == "set_attributes" && await AttributeProblemAsync(req.Type!, req.ObjectId!.Value, req.Attributes!.Value, ct) is { } problem)
            {
                return PlanWrite.Fail<PlanOperationView>(PlanWriteFailure.Invalid, problem);
            }
            payload = req.Kind switch
            {
                "set_lifecycle" => System.Text.Json.JsonSerializer.Serialize(new { type = req.Type, id = req.ObjectId, lifecycle = req.Lifecycle }),
                "set_attributes" => System.Text.Json.JsonSerializer.Serialize(new { type = req.Type, id = req.ObjectId, attributes = req.Attributes }),
                _ => System.Text.Json.JsonSerializer.Serialize(new { type = req.Type, id = req.ObjectId, name = req.Name }),
            };
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

    private static readonly System.Text.Json.JsonSerializerOptions OmitNull =
        new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

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

        // Attributes of a new site or cable must fit the type's schema (#211); nulls mean nothing on a new object.
        var attributes = req.Attributes is { } given
            ? System.Text.Json.Nodes.JsonNode.Parse(given.GetRawText())!.AsObject() : null;
        foreach (var key in attributes?.Where(p => p.Value is null).Select(p => p.Key).ToList() ?? [])
        {
            attributes!.Remove(key);
        }
        if (attributes is { Count: > 0 } && req.Kind is "create_site" or "create_cable")
        {
            var (objectType, typeKey) = req.Kind == "create_site" ? ("site", req.SiteType!) : ("cable", req.TypeKey!);
            using var doc = System.Text.Json.JsonDocument.Parse(attributes.ToJsonString());
            if (PlannedAttributes.Problems(objectType, typeKey, doc.RootElement) is { Count: > 0 } problems)
            {
                return (null, $"Attributen passar inte typens schema: {string.Join("; ", problems)}");
            }
        }
        else
        {
            attributes = null;
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
                        attributes,
                    }, OmitNull), null);
                }
            case "create_equipment":
                {
                    if (await SiteProblem(req.SiteId!.Value) is { } equipmentSite)
                    {
                        return (null, equipmentSite);
                    }
                    var rack = string.IsNullOrWhiteSpace(req.Rack) ? null : req.Rack.Trim();
                    if (req.Position is { } position && await RackProblemAsync(view, req.SiteId!.Value, rack, req.TypeKey!, position, ct) is { } rackProblem)
                    {
                        return (null, rackProblem);
                    }
                    return (System.Text.Json.JsonSerializer.Serialize(new
                    {
                        site = req.SiteId,
                        typeKey = req.TypeKey,
                        name = req.Name!.Trim(),
                        rack,
                        room = string.IsNullOrWhiteSpace(req.Room) ? null : req.Room.Trim(),
                        position = req.Position,
                    }), null);
                }
            default:
                foreach (var site in new[] { req.ASiteId!.Value, req.BSiteId!.Value })
                {
                    if (await SiteProblem(site) is { } cableSite)
                    {
                        return (null, cableSite);
                    }
                }
                return (System.Text.Json.JsonSerializer.Serialize(new { a = req.ASiteId, b = req.BSiteId, typeKey = req.TypeKey, attributes }, OmitNull), null);
        }
    }

    /// <summary>
    /// Checks a cable split (#168) and gives its payload: the cable exists in the view and is visible, is not split twice,
    /// the site lies along it (not at an end), and no conductor to be terminated carries a circuit. The payload keeps the
    /// conductors' ends and the two parts' geometry for the map.
    /// </summary>
    private async Task<(string? Payload, string? Error)> SplitAsync(UserScope scope, PlanView view, AddOperationRequest req,
        CancellationToken ct)
    {
        var cableId = req.CableId!.Value;
        var site = req.SiteId!.Value;
        if (!await PlanSql.ObjectVisibleAsync(db, "cable", cableId, scope, ct) || !view.Graph.TryGetCable(cableId, out _))
        {
            return (null, $"Kabel {cableId} finns inte.");
        }
        if (view.Chain.Operations.Any(o => o.Kind == "split_cable" && o.Payload.GetProperty("cable").GetInt64() == cableId))
        {
            return (null, "Kabeln kapas redan i planen.");
        }
        double x, y;
        if (site < 0)
        {
            if (view.Chain.Operations.FirstOrDefault(o => o.Kind == "create_site" && Planned.ObjectId(o.Id) == site) is not { } created)
            {
                return (null, $"Den planerade siten {site} finns inte i planen.");
            }
            (x, y) = (created.Payload.GetProperty("x").GetDouble(), created.Payload.GetProperty("y").GetDouble());
        }
        else
        {
            if (!await PlanSql.ObjectVisibleAsync(db, "site", site, scope, ct))
            {
                return (null, $"Site {site} finns inte.");
            }
            var position = (await PlanSql.SitePositionsAsync(db, [site], ct)).Single();
            (x, y) = (position.X, position.Y);
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        string code, typeKey;
        long aSite, bSite;
        double distance, fraction;
        double[][] lineA, lineB;
        await using (var cmd = new NpgsqlCommand("""
            WITH c AS (
                SELECT c.code, t.key, c.a_site_id, c.b_site_id, c.geom, ST_SetSRID(ST_MakePoint($2, $3), 3006) AS p
                FROM cable c JOIN cable_type t ON t.id = c.cable_type_id WHERE c.id = $1),
            f AS (SELECT c.*, ST_LineLocatePoint(c.geom, c.p) AS f, ST_Distance(c.geom, c.p) AS d FROM c)
            SELECT code, key, a_site_id, b_site_id, d, f,
                   ARRAY(SELECT ARRAY[round(ST_X(g.geom)), round(ST_Y(g.geom))]
                         FROM ST_DumpPoints(ST_Simplify(ST_MakeLine(ST_LineSubstring(geom, 0, greatest(f, 0.0001)), p), 10)) g ORDER BY g.path),
                   ARRAY(SELECT ARRAY[round(ST_X(g.geom)), round(ST_Y(g.geom))]
                         FROM ST_DumpPoints(ST_Simplify(ST_MakeLine(p, ST_LineSubstring(geom, least(f, 0.9999), 1)), 10)) g ORDER BY g.path)
            FROM f
            """, conn))
        {
            cmd.Parameters.Add(new() { Value = cableId });
            cmd.Parameters.Add(new() { Value = x });
            cmd.Parameters.Add(new() { Value = y });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            (code, typeKey, aSite, bSite, distance, fraction) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetDouble(4), reader.GetDouble(5));
            lineA = Coordinates((double[,])reader.GetValue(6));
            lineB = Coordinates((double[,])reader.GetValue(7));
        }
        if (site == aSite || site == bSite)
        {
            return (null, "Siten är redan en av kabelns ändar.");
        }
        if (distance > MaxSplitDistance)
        {
            return (null, $"Siten ligger {distance:0} m från kabeln; den får ligga högst {MaxSplitDistance:0} m från den.");
        }
        if (fraction is < 0.001 or > 0.999)
        {
            return (null, "Siten ligger vid kabelns ände. Koppla den i stället för att kapa kabeln.");
        }

        var conductors = new List<SplitConductor>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT cd.number, a.terminal_id, b.terminal_id FROM conductor cd
            JOIN conductor_end a ON a.conductor_id = cd.id AND a.side = 'A'
            JOIN conductor_end b ON b.conductor_id = cd.id AND b.side = 'B'
            WHERE cd.cable_id = $1 ORDER BY cd.number
            """, conn))
        {
            cmd.Parameters.Add(new() { Value = cableId });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                conductors.Add(new SplitConductor(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }
        if (conductors.Count is 0 or > CableSplit.MaxConductors)
        {
            return (null, $"Kabeln har {conductors.Count} ledare; en kapning klarar 1–{CableSplit.MaxConductors}.");
        }
        var terminate = (req.Terminate ?? []).Distinct().Order().ToArray();
        if (terminate.FirstOrDefault(t => conductors.All(c => c.Number != t)) is var unknown and not 0)
        {
            return (null, $"Kabeln har ingen ledare {unknown}.");
        }
        // A terminated conductor ends in the site: a circuit along it would break, so it has to be moved first.
        var busy = conductors.Where(c => terminate.Contains(c.Number) && new[] { c.A, c.B }.Any(end =>
            view.Graph.TryGetNode(end, out var node) && view.Graph.CircuitsThrough(node).Length > 0)).Select(c => c.Number).ToList();
        if (busy.Count > 0)
        {
            return (null, $"Ledare {string.Join(", ", busy)} bär kretsar och kan inte termineras i siten. Flytta kretsarna först, eller skarva igenom dem.");
        }
        return (System.Text.Json.JsonSerializer.Serialize(new
        {
            cable = cableId,
            code,
            typeKey,
            site,
            aSite,
            bSite,
            conductors = conductors.Select(c => new long[] { c.Number, c.A, c.B }),
            terminate,
            lineA,
            lineB,
        }), null);
    }

    /// <summary>
    /// Checks a stated conductor usage (#238, ADR-0014) and gives its payload: the cable is visible, the conductors exist, and
    /// none of them is lit (in a circuit in service), since lit is derived and never stated.
    /// </summary>
    private async Task<(string? Payload, string? Error)> ConductorUsageAsync(UserScope scope, AddOperationRequest req, CancellationToken ct)
    {
        var cable = req.ObjectId!.Value;
        if (!await PlanSql.ObjectVisibleAsync(db, "cable", cable, scope, ct))
        {
            return (null, $"Kabel {cable} finns inte.");
        }
        var numbers = req.Conductors!.Distinct().Order().ToArray();
        await using var cmd = db.CreateCommand("""
            SELECT k.number, EXISTS (SELECT 1 FROM conductor_end e JOIN circuit_hop h ON h.terminal_id = e.terminal_id
                                     JOIN circuit r ON r.id = h.circuit_id AND r.lifecycle = 'in_service' WHERE e.conductor_id = k.id)
            FROM conductor k WHERE k.cable_id = $1 AND k.number = ANY($2)
            """);
        cmd.Parameters.Add(new() { Value = cable });
        cmd.Parameters.Add(new() { Value = numbers });
        var found = new Dictionary<int, bool>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                found[reader.GetInt32(0)] = reader.GetBoolean(1);
            }
        }
        if (numbers.Where(n => !found.ContainsKey(n)).ToList() is { Count: > 0 } missing)
        {
            return (null, $"Kabeln har inga ledare {string.Join(", ", missing)}.");
        }
        if (req.Usage is not null && found.Where(f => f.Value).Select(f => f.Key).Order().ToList() is { Count: > 0 } lit)
        {
            return (null, $"Ledare {string.Join(", ", lit)} ingår i kretsar i drift och är tända; användningen härleds då.");
        }
        return (System.Text.Json.JsonSerializer.Serialize(new { type = "cable", id = cable, conductors = numbers, usage = req.Usage }), null);
    }

    /// <summary>
    /// Checks a move (#187) and gives its payload: equipment to another rack or site, or one end of a cable to another site.
    /// The object and the target site exist and are visible, nothing else in the plan removes, splits or moves the same thing,
    /// a rack position fits, and nothing that changes site carries a circuit. What moves keeps its row; the connections on
    /// equipment that changes site and on a moved cable end go.
    /// </summary>
    private async Task<(string? Payload, string? Error)> MoveAsync(UserScope scope, PlanView view, AddOperationRequest req, CancellationToken ct)
    {
        var (type, id, site) = (req.Type!, req.ObjectId!.Value, req.SiteId!.Value);
        var end = req.End?.ToUpperInvariant();
        if (!await PlanSql.ObjectVisibleAsync(db, type, id, scope, ct)
            || !(type == "equipment" ? view.Graph.TryGetEquipment(id, out _) : view.Graph.TryGetCable(id, out _)))
        {
            return (null, $"{type} {id} finns inte.");
        }
        foreach (var o in view.Chain.Operations)
        {
            var same = o.Kind switch
            {
                "remove" => o.ObjectType == type && o.ObjectId == id || (o.ObjectType == "site" && ObjectRemoval.Objects(o.Payload) is var taken
                    && (type == "equipment" ? taken.Equipment : taken.Cables).Contains(id)),
                "split_cable" => type == "cable" && o.Payload.GetProperty("cable").GetInt64() == id,
                "move" => o.ObjectType == type && o.ObjectId == id
                    && (type == "equipment" || string.Equals(o.Payload.GetProperty("end").GetString(), end, StringComparison.Ordinal)),
                _ => false,
            };
            if (same)
            {
                return (null, "Objektet tas bort, kapas eller flyttas redan i planen.");
            }
        }

        string siteCode;
        if (site < 0)
        {
            if (view.Chain.Operations.FirstOrDefault(o => o.Kind == "create_site" && Planned.ObjectId(o.Id) == site) is not { } created)
            {
                return (null, $"Den planerade siten {site} finns inte i planen.");
            }
            siteCode = created.Payload.GetProperty("code").GetString()!;
        }
        else
        {
            if (!await PlanSql.ObjectVisibleAsync(db, "site", site, scope, ct))
            {
                return (null, $"Site {site} finns inte.");
            }
            await using var cmd = db.CreateCommand("SELECT code FROM site WHERE id = $1");
            cmd.Parameters.Add(new() { Value = site });
            siteCode = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }

        if (type == "equipment")
        {
            await using var cmd = db.CreateCommand("""
                SELECT e.site_id, e.name, t.key, s.code, e.lifecycle::text FROM equipment e
                JOIN equipment_type t ON t.id = e.equipment_type_id JOIN site s ON s.id = e.site_id WHERE e.id = $1
                """);
            cmd.Parameters.Add(new() { Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(4) == "removed")
            {
                return (null, "Utrustningen finns inte längre.");
            }
            var (fromSite, name, typeKey, fromCode) = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            await reader.CloseAsync();
            var rack = string.IsNullOrWhiteSpace(req.Rack) ? null : req.Rack.Trim();
            if (site == fromSite && rack is null && req.Position is null)
            {
                return (null, "Utrustningen sitter redan på siten: ange rack eller position, eller en annan site.");
            }
            if (req.Position is { } position && await RackProblemAsync(view, site, rack, typeKey, position, ct, except: id) is { } rackProblem)
            {
                return (null, rackProblem);
            }
            if (site != fromSite)
            {
                var circuits = ObjectRemoval.Circuits(view.Graph, ObjectRemoval.Nodes(view.Graph, [id], []));
                if (circuits.Count > 0)
                {
                    return (null, $"{circuits.Count} kretsar går genom utrustningen. Flytta dem först, annars bryts tjänsterna.");
                }
            }
            return (System.Text.Json.JsonSerializer.Serialize(new
            {
                type,
                id,
                code = name,
                site,
                siteCode,
                fromSite,
                fromCode,
                rack,
                room = string.IsNullOrWhiteSpace(req.Room) ? null : req.Room.Trim(),
                position = req.Position,
            }), null);
        }

        await using var cableCmd = db.CreateCommand("""
            SELECT c.a_site_id, c.b_site_id, c.code, c.lifecycle::text, a.code, b.code,
                   ARRAY(SELECT ce.terminal_id FROM conductor_end ce JOIN conductor k ON k.id = ce.conductor_id
                         WHERE k.cable_id = c.id AND ce.side = $2 ORDER BY ce.terminal_id)
            FROM cable c JOIN site a ON a.id = c.a_site_id JOIN site b ON b.id = c.b_site_id WHERE c.id = $1
            """);
        cableCmd.Parameters.Add(new() { Value = id });
        cableCmd.Parameters.Add(new() { Value = end!, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await using var cableReader = await cableCmd.ExecuteReaderAsync(ct);
        if (!await cableReader.ReadAsync(ct) || cableReader.GetString(3) == "removed")
        {
            return (null, "Kabeln finns inte längre.");
        }
        var (a, b, code) = (cableReader.GetInt64(0), cableReader.GetInt64(1), cableReader.GetString(2));
        var (fromEnd, other) = end == "A" ? (a, b) : (b, a);
        var fromEndCode = end == "A" ? cableReader.GetString(4) : cableReader.GetString(5);
        var terminals = cableReader.GetFieldValue<long[]>(6);
        if (site == fromEnd)
        {
            return (null, $"Änden sitter redan på {siteCode}.");
        }
        if (site == other)
        {
            return (null, "Kabeln skulle sluta på samma site i båda ändar.");
        }
        var carried = ObjectRemoval.Circuits(view.Graph, [.. terminals.Where(t => view.Graph.TryGetNode(t, out _)).Select(t =>
        {
            view.Graph.TryGetNode(t, out var node);
            return node;
        })]);
        if (carried.Count > 0)
        {
            return (null, $"{carried.Count} kretsar går genom änden. Flytta dem först, annars bryts tjänsterna.");
        }
        return (System.Text.Json.JsonSerializer.Serialize(new
        {
            type,
            id,
            code,
            end,
            site,
            siteCode,
            fromSite = fromEnd,
            fromCode = fromEndCode,
            otherSite = other,
            terminals,
        }), null);
    }

    /// <summary>
    /// Checks a removal (#172) and gives its payload: the object exists, is visible and not removed already, and nothing it
    /// takes along carries a circuit. A site takes its equipment and the cables that end at it.
    /// </summary>
    private async Task<(string? Payload, string? Error)> RemovalAsync(UserScope scope, PlanView view, AddOperationRequest req, CancellationToken ct)
    {
        var (type, id) = (req.Type!, req.ObjectId!.Value);
        if (!await PlanSql.ObjectVisibleAsync(db, type, id, scope, ct))
        {
            return (null, $"{type} {id} finns inte.");
        }
        if (view.Chain.Operations.Any(o => o.Kind == "remove" && o.Payload.GetProperty("type").GetString() == type && o.Payload.GetProperty("id").GetInt64() == id))
        {
            return (null, "Objektet tas redan bort i planen.");
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand($"SELECT lifecycle::text FROM {type} WHERE id = $1", conn))
        {
            cmd.Parameters.Add(new() { Value = id });
            if ((string?)await cmd.ExecuteScalarAsync(ct) == "removed")
            {
                return (null, "Objektet är redan borttaget.");
            }
        }
        long[] equipment = type == "equipment" ? [id] : [], cables = type == "cable" ? [id] : [];
        if (type == "site")
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT ARRAY(SELECT id FROM equipment WHERE site_id = $1 AND lifecycle <> 'removed' ORDER BY id),
                       ARRAY(SELECT id FROM cable WHERE (a_site_id = $1 OR b_site_id = $1) AND lifecycle <> 'removed' ORDER BY id)
                """, conn);
            cmd.Parameters.Add(new() { Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            (equipment, cables) = (reader.GetFieldValue<long[]>(0), reader.GetFieldValue<long[]>(1));
        }
        var circuits = ObjectRemoval.Circuits(view.Graph, ObjectRemoval.Nodes(view.Graph, equipment, cables));
        if (circuits.Count > 0)
        {
            var services = circuits.SelectMany(c => view.Graph.ServicesOf(c).ToArray()).Distinct().Count();
            return (null, $"{circuits.Count} kretsar går genom det som tas bort" + (services > 0 ? $" och bär {services} tjänster" : "") +
                ". Flytta dem först, annars bryts tjänsterna.");
        }
        return (System.Text.Json.JsonSerializer.Serialize(type == "site"
            ? (object)new { type, id, equipment, cables }
            : new { type, id }), null);
    }

    /// <summary>
    /// Whether equipment of the type fits the rack at the position (#173): inside the rack's height, and clear of what
    /// production and the plan's view put there (equipment the plan removes frees its units). The rack is the named one,
    /// or the site's first, as the apply picks it.
    /// </summary>
    private async Task<string?> RackProblemAsync(PlanView view, long site, string? rack, string typeKey, int position, CancellationToken ct,
        long? except = null)
    {
        if (Cmdb.Catalog.TypeCatalog.Current.Find(typeKey)?.RackUnits is not { } units)
        {
            return "Modellen monteras inte i rack, så den har ingen position.";
        }
        var height = Cmdb.Database.RackStacking.DefaultRackUnits;
        var taken = new List<(int From, int To, string Name)>();
        string? rackName = rack;
        if (site > 0)
        {
            await using var cmd = db.CreateCommand("""
                WITH r AS (SELECT id, name, coalesce(rack_units, 42) AS h FROM location
                           WHERE site_id = $1 AND kind = 'rack' AND ($2::text IS NULL OR name = $2) ORDER BY id LIMIT 1)
                SELECT r.name, r.h, e.id, e.name, e.rack_position, t.rack_units
                FROM r LEFT JOIN equipment e ON e.location_id = r.id AND e.rack_position IS NOT NULL AND e.lifecycle <> 'removed'
                LEFT JOIN equipment_type t ON t.id = e.equipment_type_id
                """);
            cmd.Parameters.Add(new() { Value = site });
            cmd.Parameters.Add(new() { Value = (object?)rack ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            var removed = view.Chain.Operations.Where(o => o.Kind == "remove").SelectMany(o => ObjectRemoval.Objects(o.Payload).Equipment).ToHashSet();
            if (except is { } moving)
            {
                removed.Add(moving);
            }
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                (rackName, height) = (reader.GetString(0), reader.GetInt32(1));
                if (!reader.IsDBNull(2) && !removed.Contains(reader.GetInt64(2)))
                {
                    var from = reader.GetInt16(4);
                    taken.Add((from, from + (reader.IsDBNull(5) ? 1 : reader.GetInt16(5)) - 1, reader.GetString(3)));
                }
            }
        }
        // What the plan already puts in the same rack at a position.
        foreach (var o in view.Chain.Operations.Where(o => o.Kind == "create_equipment" && o.Payload.GetProperty("site").GetInt64() == site))
        {
            var p = o.Payload;
            var otherRack = p.TryGetProperty("rack", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String ? r.GetString() : null;
            if ((otherRack ?? rackName) != (rack ?? rackName) || !p.TryGetProperty("position", out var at) || at.ValueKind != System.Text.Json.JsonValueKind.Number)
            {
                continue;
            }
            var otherUnits = Cmdb.Catalog.TypeCatalog.Current.Find(p.GetProperty("typeKey").GetString()!)?.RackUnits ?? 1;
            taken.Add((at.GetInt32(), at.GetInt32() + otherUnits - 1, p.GetProperty("name").GetString()!));
        }
        var (low, high) = (position, position + units - 1);
        if (low < 1 || high > height)
        {
            return $"Racket har {height} U; en utrustning på {units} U ryms på position 1–{height - units + 1}.";
        }
        if (taken.FirstOrDefault(t => t.From <= high && low <= t.To) is { Name: not null } clash)
        {
            return $"U {low}–{high} krockar med {clash.Name} på U {clash.From}–{clash.To}.";
        }
        return null;
    }

    /// <summary>How far from the cable a site inserted into it may lie (#168).</summary>
    private const double MaxSplitDistance = 2_000;

    private static double[][] Coordinates(double[,] points) =>
        [.. Enumerable.Range(0, points.GetLength(0)).Select(i => new[] { points[i, 0], points[i, 1] })];

    /// <summary>
    /// Attributes must fit their type's schema in the catalog: equipment its model's (#27), sites and cables their
    /// type's when it has one (#211). The current attributes with the change merged in (null removes a key) are
    /// validated before the change enters the plan.
    /// </summary>
    private async Task<string?> AttributeProblemAsync(string type, long id, System.Text.Json.JsonElement patch, CancellationToken ct)
    {
        var sql = type switch
        {
            "equipment" => "SELECT t.key, jsonb_strip_nulls(o.attributes || $2::jsonb)::text FROM equipment o JOIN equipment_type t ON t.id = o.equipment_type_id WHERE o.id = $1",
            "site" => "SELECT o.site_type, jsonb_strip_nulls(o.attributes || $2::jsonb)::text FROM site o WHERE o.id = $1",
            "cable" => "SELECT t.key, jsonb_strip_nulls(o.attributes || $2::jsonb)::text FROM cable o JOIN cable_type t ON t.id = o.cable_type_id WHERE o.id = $1",
            _ => null,
        };
        if (sql is null)
        {
            return null;
        }
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = patch.GetRawText() });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return $"{type} {id} finns inte.";
        }
        using var merged = System.Text.Json.JsonDocument.Parse(reader.GetString(1));
        var errors = Cmdb.Catalog.TypeCatalog.Current.ValidateAttributes(type, reader.GetString(0), merged.RootElement);
        return errors.Count == 0 ? null
            : $"Attributen passar inte {(type == "equipment" ? "modellens" : "typens")} schema: {string.Join("; ", errors)}";
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

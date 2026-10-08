using System.Globalization;
using System.Security.Claims;
using Cmdb.Api.Auth;
using Cmdb.Catalog;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>A port or a conductor end, in order: front-panel position, or conductor number.</summary>
public sealed record PlanTerminal(long Terminal, string Name, int Number);

/// <summary>Ports from a start port to conductors from a start conductor on one side of a cable (#26).</summary>
public sealed class PatternRequest
{
    public long Id { get; set; }
    public long EquipmentId { get; set; }

    /// <summary>The first port: its name or front-panel position.</summary>
    public string FromPort { get; set; } = "1";

    public int Count { get; set; } = 1;
    public int PortStep { get; set; } = 1;
    public long CableId { get; set; }
    public int FromConductor { get; set; } = 1;
    public int ConductorStep { get; set; } = 1;

    /// <summary>A or B: the end of the cable at the equipment's site.</summary>
    public string Side { get; set; } = "A";

    public string Kind { get; set; } = "splice";
}

public sealed class TemplateRequest
{
    public long Id { get; set; }
    public string TemplateKey { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A proposed termination of a cable (#26): free ODF ports at each end, fibre k to the k-th free port.</summary>
public sealed record TerminationSide(string Side, long SiteId, string SiteCode, long? EquipmentId, string? Equipment, int Fibres,
    IReadOnlyList<AddOperationRequest> Operations);

public sealed record Termination(long CableId, IReadOnlyList<TerminationSide> Sides);

/// <summary>
/// Mass provisioning in a plan (#26): several operations added together, all or none. Site templates and port-to-fibre
/// patterns expand into ordinary operations, so everything is checked as one by one, and a cable gets a suggested
/// termination on free ODF ports.
/// </summary>
public sealed class PlanPatterns(RequestDb db, PlanWrites writes, PlanViews views, GraphHolder holder)
{
    public const int MaxBatch = 500;

    /// <summary>Adds the operations in order; if one fails, the ones added before it are removed again.</summary>
    public async Task<PlanWrite<List<PlanOperationView>>> BatchAsync(ClaimsPrincipal user, UserScope scope, long planId,
        IReadOnlyList<AddOperationRequest> operations, CancellationToken ct)
    {
        if (operations.Count is 0 or > MaxBatch)
        {
            return PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.Invalid, $"Ge 1–{MaxBatch} operationer.");
        }
        var added = new List<PlanOperationView>();
        foreach (var op in operations)
        {
            op.Id = planId;
            var result = await writes.AddAsync(user, scope, op, ct);
            if (result.Value is null)
            {
                await RemoveAsync(planId, [.. added.Select(a => a.Id)], ct);
                return PlanWrite.Fail<List<PlanOperationView>>(result.Failure, $"Operation {added.Count + 1}: {result.Error} Inget lades till.");
            }
            added.Add(result.Value);
        }
        return new(added);
    }

    /// <summary>A site from a template: the site, its equipment in racks, and the internal cabling, as one batch.</summary>
    public async Task<PlanWrite<List<PlanOperationView>>> TemplateAsync(ClaimsPrincipal user, UserScope scope, TemplateRequest req, CancellationToken ct)
    {
        if (SiteTemplates.Current.Find(req.TemplateKey) is not { } template)
        {
            return PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.Invalid, $"Mallen {req.TemplateKey} finns inte.");
        }
        var code = req.Code.Trim();
        // Planned ids come from operation ids, which exist only once added: the site first, then the equipment, then
        // the connections between their ports.
        var site = await BatchAsync(user, scope, req.Id, [new AddOperationRequest
        {
            Kind = "create_site", Code = code, Name = req.Name.Trim(), SiteType = template.SiteType, X = req.X, Y = req.Y,
        }], ct);
        if (site.Value is null)
        {
            return site;
        }
        var siteId = site.Value[0].Target!.Id;
        var equipment = await BatchAsync(user, scope, req.Id, [.. template.Equipment.Select(e => new AddOperationRequest
        {
            Kind = "create_equipment", SiteId = siteId, TypeKey = e.TypeKey, Name = e.Name.Replace("{code}", code, StringComparison.Ordinal), Rack = e.Rack,
        })], ct);
        if (equipment.Value is null)
        {
            await RemoveAsync(req.Id, [site.Value[0].Id], ct);
            return equipment;
        }
        var ops = template.Equipment.Select((e, i) => (e.Ref, Op: equipment.Value[i].Id, Type: TypeCatalog.Current.Find(e.TypeKey)!))
            .ToDictionary(x => x.Ref);
        long Port(string equipmentRef, string port)
        {
            var (_, op, type) = ops[equipmentRef];
            return Planned.Terminal(op, PortExpansion.Expand(type).Single(p => p.Name == port).Position);
        }
        var connections = template.Connections.Count == 0
            ? new PlanWrite<List<PlanOperationView>>([])
            : await BatchAsync(user, scope, req.Id, [.. template.Connections.Select(c => new AddOperationRequest
            {
                Kind = "connect", A = Port(c.From, c.FromPort), B = Port(c.To, c.ToPort), ConnectionKind = c.Kind,
            })], ct);
        if (connections.Value is null)
        {
            await RemoveAsync(req.Id, [site.Value[0].Id, .. equipment.Value.Select(e => e.Id)], ct);
            return connections;
        }
        return new([.. site.Value, .. equipment.Value, .. connections.Value]);
    }

    /// <summary>Ports to conductor ends by pattern: port start + i × step to conductor start + i × step, i &lt; count.</summary>
    public async Task<PlanWrite<List<PlanOperationView>>> PatternAsync(ClaimsPrincipal user, UserScope scope, PatternRequest req, CancellationToken ct)
    {
        if (req.Count is < 1 or > 288 || req.PortStep < 1 || req.ConductorStep < 1 || req.Side is not ("A" or "B")
            || req.Kind is not ("splice" or "termination" or "patch"))
        {
            return PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.Invalid,
                "count är 1–288, stegen minst 1, side A eller B och kind splice, termination eller patch.");
        }
        var ports = await PortsAsync(req.Id, req.EquipmentId, scope, ct);
        var ends = await ConductorEndsAsync(req.Id, req.CableId, req.Side, scope, ct);
        var first = ports.FindIndex(p => p.Name == req.FromPort
            || (int.TryParse(req.FromPort, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && p.Number == n));
        var firstEnd = ends.FindIndex(e => e.Number == req.FromConductor);
        if (ports.Count == 0 || ends.Count == 0 || first < 0 || firstEnd < 0)
        {
            return PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.Invalid,
                ports.Count == 0 ? "Utrustningen finns inte." : ends.Count == 0 ? "Kabeln finns inte." : first < 0 ? $"Porten {req.FromPort} finns inte." : $"Ledare {req.FromConductor} finns inte.");
        }
        var lastPort = first + ((req.Count - 1) * req.PortStep);
        var lastEnd = firstEnd + ((req.Count - 1) * req.ConductorStep);
        if (lastPort >= ports.Count || lastEnd >= ends.Count)
        {
            return PlanWrite.Fail<List<PlanOperationView>>(PlanWriteFailure.Invalid,
                lastPort >= ports.Count ? $"Utrustningen har bara {ports.Count} portar." : $"Kabeln har bara {ends.Count} ledare.");
        }
        return await BatchAsync(user, scope, req.Id, [.. Enumerable.Range(0, req.Count).Select(i => new AddOperationRequest
        {
            Kind = "connect", A = ports[first + (i * req.PortStep)].Terminal, B = ends[firstEnd + (i * req.ConductorStep)].Terminal,
            ConnectionKind = req.Kind,
        })], ct);
    }

    /// <summary>
    /// A termination for the cable at both ends: at each end's site, the ODF with the most ports free at the back
    /// (no splice) in the plan's view, fibre k to its k-th free port.
    /// </summary>
    public async Task<Termination?> TerminationAsync(UserScope scope, long planId, long cableId, CancellationToken ct)
    {
        if (await views.GetAsync(holder.Require(), planId, scope, ct) is not { } view)
        {
            return null;
        }
        var ends = await CableSitesAsync(view.Chain, cableId, scope, ct);
        if (ends is null)
        {
            return null;
        }
        var sides = new List<TerminationSide>();
        foreach (var (side, siteId, siteCode) in ends)
        {
            var fibres = await ConductorEndsAsync(planId, cableId, side, scope, ct);
            var best = (Id: (long?)null, Name: (string?)null, Free: new List<PlanTerminal>());
            foreach (var (id, name) in await OdfsAtAsync(view.Chain, siteId, scope, ct))
            {
                var free = (await PortsAsync(planId, id, scope, ct))
                    .Where(p => !view.Graph.TryGetNode(p.Terminal, out var node) || !view.Graph.NeighbourKinds(node).Contains(EdgeKind.Splice))
                    .ToList();
                if (free.Count > best.Free.Count)
                {
                    best = (id, name, free);
                }
            }
            var unspliced = fibres.Where(f => !view.Graph.TryGetNode(f.Terminal, out var node)
                || !view.Graph.NeighbourKinds(node).Contains(EdgeKind.Splice)).ToList();
            var pairs = unspliced.Zip(best.Free).Select(p => new AddOperationRequest
            {
                Kind = "connect",
                A = p.Second.Terminal,
                B = p.First.Terminal,
                ConnectionKind = "splice",
            }).ToList();
            sides.Add(new TerminationSide(side, siteId, siteCode, best.Id, best.Name, pairs.Count, pairs));
        }
        return new Termination(cableId, sides);
    }

    /// <summary>The equipment's ports in front-panel order; planned equipment's from its model's template.</summary>
    public async Task<List<PlanTerminal>> PortsAsync(long planId, long equipmentId, UserScope scope, CancellationToken ct)
    {
        if (equipmentId < 0)
        {
            var chain = await PlanViews.LoadChainAsync(db, planId, ct);
            var op = chain?.Operations.FirstOrDefault(o => o.Kind == "create_equipment" && Planned.ObjectId(o.Id) == equipmentId);
            return op is null || TypeCatalog.Current.Find(op.Payload.GetProperty("typeKey").GetString()!) is not { } type ? []
                : [.. PortExpansion.Expand(type).Select(p => new PlanTerminal(Planned.Terminal(op.Id, p.Position), p.Name, p.Position))];
        }
        await using var cmd = db.CreateCommand($"""
            SELECT p.terminal_id, p.name, p.position FROM port p JOIN equipment e ON e.id = p.equipment_id
            WHERE e.id = $1 AND {ScopeSql.Site("e.site_id", 2)} ORDER BY p.position
            """);
        cmd.Parameters.Add(new() { Value = equipmentId });
        cmd.Parameters.Add(scope.Parameter());
        return await ReadAsync(cmd, ct);
    }

    /// <summary>The conductor ends on one side of a cable, by conductor number; a planned cable's from its type.</summary>
    public async Task<List<PlanTerminal>> ConductorEndsAsync(long planId, long cableId, string side, UserScope scope, CancellationToken ct)
    {
        if (cableId < 0)
        {
            var chain = await PlanViews.LoadChainAsync(db, planId, ct);
            var op = chain?.Operations.FirstOrDefault(o => o.Kind == "create_cable" && Planned.ObjectId(o.Id) == cableId);
            if (op is null)
            {
                return [];
            }
            var count = Planned.ConductorCount(op.Payload.GetProperty("typeKey").GetString()!);
            return [.. Enumerable.Range(1, count).Select(k =>
                new PlanTerminal(Planned.Terminal(op.Id, side == "A" ? (2 * k) - 1 : 2 * k), $"ledare {k} ({side})", k))];
        }
        await using var cmd = db.CreateCommand($"""
            SELECT ce.terminal_id, 'ledare ' || k.number || ' (' || trim(ce.side) || ')', k.number
            FROM conductor k JOIN conductor_end ce ON ce.conductor_id = k.id
            WHERE k.cable_id = $1 AND ce.side = $2 AND {ScopeSql.Cable("k.cable_id", 3)} ORDER BY k.number
            """);
        cmd.Parameters.Add(new() { Value = cableId });
        cmd.Parameters.Add(new() { Value = side });
        cmd.Parameters.Add(scope.Parameter());
        return await ReadAsync(cmd, ct);
    }

    private async Task<List<(string Side, long SiteId, string SiteCode)>?> CableSitesAsync(PlanChain chain, long cableId, UserScope scope,
        CancellationToken ct)
    {
        long a, b;
        if (cableId < 0)
        {
            var op = chain.Operations.FirstOrDefault(o => o.Kind == "create_cable" && Planned.ObjectId(o.Id) == cableId);
            if (op is null)
            {
                return null;
            }
            (a, b) = (op.Payload.GetProperty("a").GetInt64(), op.Payload.GetProperty("b").GetInt64());
        }
        else
        {
            await using var cmd = db.CreateCommand($"SELECT a_site_id, b_site_id FROM cable c WHERE c.id = $1 AND {ScopeSql.Cable("c.id", 2)}");
            cmd.Parameters.Add(new() { Value = cableId });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            (a, b) = (reader.GetInt64(0), reader.GetInt64(1));
        }
        var names = await PlannedNames.BuildAsync(db, chain.Operations, ct);
        async Task<string> Code(long site)
        {
            if (site < 0)
            {
                return names.Objects.TryGetValue(("site", site), out var planned) ? planned.Code : $"#{site}";
            }
            await using var cmd = db.CreateCommand("SELECT code FROM site WHERE id = $1");
            cmd.Parameters.Add(new() { Value = site });
            return (string?)await cmd.ExecuteScalarAsync(ct) ?? $"#{site}";
        }
        return [("A", a, await Code(a)), ("B", b, await Code(b))];
    }

    /// <summary>ODFs at a site: production's and those the plan creates there.</summary>
    private async Task<List<(long Id, string Name)>> OdfsAtAsync(PlanChain chain, long siteId, UserScope scope, CancellationToken ct)
    {
        var odfs = chain.Operations
            .Where(o => o.Kind == "create_equipment" && o.Payload.GetProperty("site").GetInt64() == siteId
                && TypeCatalog.Current.Find(o.Payload.GetProperty("typeKey").GetString()!)?.Category == "odf")
            .Select(o => (Planned.ObjectId(o.Id), o.Payload.GetProperty("name").GetString()!))
            .ToList();
        if (siteId > 0)
        {
            await using var cmd = db.CreateCommand($"""
                SELECT e.id, e.name FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id
                WHERE e.site_id = $1 AND t.category = 'odf' AND {ScopeSql.Site("e.site_id", 2)} ORDER BY e.id
                """);
            cmd.Parameters.Add(new() { Value = siteId });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                odfs.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }
        return odfs;
    }

    internal async Task RemoveAsync(long planId, long[] operations, CancellationToken ct)
    {
        if (operations.Length == 0)
        {
            return;
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("DELETE FROM plan_operation WHERE plan_id = $1 AND id = ANY($2)", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = planId });
            cmd.Parameters.Add(new() { Value = operations });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await PlanSql.TouchAsync(conn, tx, planId, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<List<PlanTerminal>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<PlanTerminal>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PlanTerminal(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return list;
    }
}

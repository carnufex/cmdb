using System.ComponentModel;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.Classifications;

/// <param name="Title">What it does, in words.</param>
/// <param name="Operation">The operation to add to the plan, as <c>POST /api/plans/{id}/operations</c> takes it.</param>
public sealed record SuggestedOperation(string Title, object Operation);

/// <param name="FreeRackUnits">Free rack units at the site, and whether they cover the equipment the plan wants to put there.</param>
public sealed record AlternativeSite(ObjectRef Site, double DistanceM, int FreeRackUnits, int Cables);

/// <param name="Before">The derived level in production; 0 for a site the plan creates.</param>
/// <param name="After">The derived level in the plan's view.</param>
/// <param name="Introduced">Rules that are not met in the plan and were met (or did not apply) in production.</param>
public sealed record SiteFinding(ObjectRef Site, int Before, int After, string AfterName, bool Raised, IReadOnlyList<DerivedReason> Because,
    IReadOnlyList<RuleResult> Unmet, IReadOnlyList<string> Introduced, IReadOnlyList<SuggestedOperation> Suggestions, IReadOnlyList<AlternativeSite> Alternatives);

/// <param name="Introduced">Requirements the plan makes unmet: what applying it would break (#179).</param>
public sealed record PlanClassificationReport(long Plan, IReadOnlyList<SiteFinding> Findings, int Introduced, double ElapsedMs);

/// <summary>
/// What a plan does to classifications (#179, ADR-0017): which sites it raises to a higher derived level (a new switch on level
/// 5 makes its site level 5), which requirements of that level are not met in the plan's view, what would meet them as
/// operations ready to add, and other sites nearby that already meet them. A requirement that was already unmet in production is
/// not the plan's doing and is not counted against it.
/// </summary>
public sealed class PlanClassification(RequestDb db, ClassificationDerivation derivation, ClassificationRules rules, PlanViews views, GraphHolder holder)
{
    private const int MaxSites = 40;
    private const int Candidates = 12;
    private const double SearchMetres = 30_000;
    private const string SchemaKey = "criticality";

    public async Task<PlanClassificationReport?> ReportAsync(UserScope scope, long planId, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (holder.Current is not { } production || await views.GetAsync(production, planId, scope, ct) is not { } view)
        {
            return null;
        }
        var chain = view.Chain.Operations;
        var findings = new List<SiteFinding>();
        foreach (var site in await SitesAsync(chain, scope, ct))
        {
            var before = site > 0 ? await derivation.DeriveAsync(scope, "site", site, SchemaKey, null, ct) : null;
            if (await derivation.DeriveAsync(scope, "site", site, SchemaKey, planId, ct) is not { } after)
            {
                continue;
            }
            var beforeUnmet = site > 0 && before is { Level: > 0 } && await rules.EvaluateAsync(scope, "site", site, SchemaKey, null, ct) is { } b
                ? b.Results.Where(r => !r.Met).Select(r => r.Rule).ToHashSet() : [];
            var report = await rules.EvaluateAsync(scope, "site", site, SchemaKey, planId, ct);
            var unmet = report?.Results.Where(r => !r.Met).ToList() ?? [];
            var introduced = unmet.Select(r => r.Rule).Where(r => !beforeUnmet.Contains(r)).ToList();
            var raised = after.Level > (before?.Level ?? 0);
            if (!raised && introduced.Count == 0)
            {
                continue;
            }
            var reference = await SiteRefAsync(site, chain, ct);
            var suggestions = unmet.Count > 0 ? await SuggestionsAsync(site, reference, unmet, chain, scope, ct) : [];
            var alternatives = unmet.Count > 0 ? await AlternativesAsync(site, after.Level, chain, scope, ct) : [];
            findings.Add(new SiteFinding(reference, before?.Level ?? 0, after.Level, after.Name, raised, after.Reasons, unmet, introduced, suggestions, alternatives));
        }
        return new PlanClassificationReport(planId, findings, findings.Sum(f => f.Introduced.Count),
            Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
    }

    /// <summary>The sites the plan touches: where it builds, splits or removes, and where it classifies something.</summary>
    private async Task<List<long>> SitesAsync(IReadOnlyList<PlanOp> chain, UserScope scope, CancellationToken ct)
    {
        var sites = new HashSet<long>();
        var equipment = new HashSet<long>();
        var plannedEquipmentSite = chain.Where(o => o.Kind == "create_equipment").ToDictionary(o => Planned.ObjectId(o.Id), o => o.Payload.GetProperty("site").GetInt64());
        foreach (var op in chain)
        {
            switch (op.Kind)
            {
                case "create_site":
                    sites.Add(Planned.ObjectId(op.Id));
                    break;
                case "create_equipment" or "split_cable":
                    sites.Add(op.Payload.GetProperty("site").GetInt64());
                    break;
                case "move":
                    sites.Add(op.Payload.GetProperty("site").GetInt64());
                    sites.Add(op.Payload.GetProperty("fromSite").GetInt64());
                    break;
                case "create_cable":
                    sites.Add(op.Payload.GetProperty("a").GetInt64());
                    sites.Add(op.Payload.GetProperty("b").GetInt64());
                    break;
                case "set_classification" or "remove" when op.ObjectType == "site":
                    sites.Add(op.ObjectId);
                    break;
                case "set_classification" or "remove" when op.ObjectType == "equipment":
                    if (op.ObjectId < 0)
                    {
                        if (plannedEquipmentSite.TryGetValue(op.ObjectId, out var s))
                        {
                            sites.Add(s);
                        }
                    }
                    else
                    {
                        equipment.Add(op.ObjectId);
                    }
                    break;
                default:
                    break;
            }
        }
        if (equipment.Count > 0)
        {
            await using var cmd = db.Source.CreateCommand("SELECT DISTINCT site_id FROM equipment WHERE id = ANY($1)");
            cmd.Parameters.Add(new() { Value = equipment.ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sites.Add(reader.GetInt64(0));
            }
        }
        var existing = sites.Where(s => s > 0).ToArray();
        var visible = new HashSet<long>();
        if (existing.Length > 0)
        {
            await using var cmd = db.Source.CreateCommand($"SELECT id FROM site s WHERE s.id = ANY($1) AND s.lifecycle <> 'removed' AND {ScopeSql.Site("s.id", 2)}");
            cmd.Parameters.Add(new() { Value = existing });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                visible.Add(reader.GetInt64(0));
            }
        }
        return [.. sites.Where(s => s < 0 || visible.Contains(s)).Order().Take(MaxSites)];
    }

    private async Task<ObjectRef> SiteRefAsync(long site, IReadOnlyList<PlanOp> chain, CancellationToken ct)
    {
        if (site < 0)
        {
            var created = chain.First(o => o.Kind == "create_site" && Planned.ObjectId(o.Id) == site).Payload;
            return new ObjectRef("site", site, created.GetProperty("code").GetString()!, created.GetProperty("name").GetString(), "planned");
        }
        await using var cmd = db.Source.CreateCommand("SELECT code, name, lifecycle::text FROM site WHERE id = $1");
        cmd.Parameters.Add(new() { Value = site });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ObjectRef("site", site, reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : new ObjectRef("site", site, $"#{site}");
    }

    /// <summary>The site's position: from the database, or from the operation that creates it.</summary>
    private async Task<(double X, double Y)?> PositionAsync(long site, IReadOnlyList<PlanOp> chain, CancellationToken ct)
    {
        if (site < 0)
        {
            return chain.FirstOrDefault(o => o.Kind == "create_site" && Planned.ObjectId(o.Id) == site) is { } op
                ? (op.Payload.GetProperty("x").GetDouble(), op.Payload.GetProperty("y").GetDouble()) : null;
        }
        var position = (await PlanSql.SitePositionsAsync(db.Source, [site], ct)).FirstOrDefault();
        return position is null ? null : (position.X, position.Y);
    }

    /// <summary>Operations that would meet what is missing, ready to add to the plan.</summary>
    private async Task<List<SuggestedOperation>> SuggestionsAsync(long site, ObjectRef reference, List<RuleResult> unmet, IReadOnlyList<PlanOp> chain,
        UserScope scope, CancellationToken ct)
    {
        var list = new List<SuggestedOperation>();
        var schema = ClassificationCatalog.Current.Find(SchemaKey)!;
        foreach (var result in unmet)
        {
            var rule = schema.RuleList.First(r => r.Id == result.Rule);
            if (rule.Type == "attribute" && site > 0)
            {
                list.Add(new SuggestedOperation($"Planera reservkraft på {reference.Code}: {rule.Attribute} {rule.Min}",
                    new { kind = "set_attributes", type = "site", objectId = site, attributes = new Dictionary<string, object> { [rule.Attribute!] = rule.Min } }));
            }
            else if (rule.Type == "cables" && await PositionAsync(site, chain, ct) is { } at)
            {
                // Cables to the nearest hubs and aggregation nodes the site has none to yet.
                var have = result.Objects.Count;
                var missing = Math.Max(rule.Min - have, 0);
                foreach (var near in await NearAsync(site, at, 2, chain, scope, ["hub", "aggregation"], ct))
                {
                    if (missing-- <= 0)
                    {
                        break;
                    }
                    list.Add(new SuggestedOperation($"Dra en kabel från {reference.Code} till {near.Site.Code} ({near.DistanceM / 1000:0.#} km)",
                        new { kind = "create_cable", aSiteId = site, bSiteId = near.Site.Id, typeKey = "fiber-12" }));
                }
            }
        }
        return list;
    }

    /// <summary>Other sites nearby that already meet the requirements of the level and have room for the equipment the plan adds.</summary>
    private async Task<List<AlternativeSite>> AlternativesAsync(long site, int level, IReadOnlyList<PlanOp> chain, UserScope scope, CancellationToken ct)
    {
        if (await PositionAsync(site, chain, ct) is not { } at)
        {
            return [];
        }
        var units = chain.Where(o => o.Kind == "create_equipment" && o.Payload.GetProperty("site").GetInt64() == site)
            .Sum(o => TypeCatalog.Current.Find(o.Payload.GetProperty("typeKey").GetString()!)?.RackUnits ?? 0);
        var found = new List<AlternativeSite>();
        foreach (var near in await NearAsync(site, at, Candidates, chain, scope, null, ct))
        {
            if (near.FreeRackUnits < units
                || await rules.EvaluateAsync(scope, "site", near.Site.Id, SchemaKey, null, ct, assumeLevel: level) is not { Unmet: 0 })
            {
                continue;
            }
            found.Add(near);
            if (found.Count == 3)
            {
                break;
            }
        }
        return found;
    }

    private async Task<List<AlternativeSite>> NearAsync(long site, (double X, double Y) at, int count, IReadOnlyList<PlanOp> chain, UserScope scope,
        string[]? types, CancellationToken ct)
    {
        var skip = chain.Where(o => o.Kind == "remove" && o.ObjectType == "site").Select(o => o.ObjectId).Append(site).ToArray();
        var list = new List<AlternativeSite>();
        await using var cmd = db.Source.CreateCommand($"""
            WITH p AS (SELECT ST_SetSRID(ST_MakePoint($1, $2), 3006) AS g)
            SELECT s.id, s.code, s.name, ST_Distance(s.geom, p.g),
                   (coalesce((SELECT sum(coalesce(l.rack_units, 42)) FROM location l WHERE l.site_id = s.id AND l.kind = 'rack'), 0)
                    - coalesce((SELECT sum(coalesce(t.rack_units, 1)) FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id
                                WHERE e.site_id = s.id AND e.rack_position IS NOT NULL AND e.lifecycle <> 'removed'), 0))::int,
                   (SELECT count(*) FROM cable c WHERE (c.a_site_id = s.id OR c.b_site_id = s.id) AND c.lifecycle = 'in_service')::int
            FROM site s, p
            WHERE s.lifecycle = 'in_service' AND NOT (s.id = ANY($3)) AND ST_DWithin(s.geom, p.g, {SearchMetres.ToString("0", System.Globalization.CultureInfo.InvariantCulture)})
              AND ($4::text[] IS NULL OR s.site_type = ANY($4)) AND {ScopeSql.Site("s.id", 5)}
            ORDER BY s.geom <-> p.g LIMIT {count}
            """);
        cmd.Parameters.Add(new() { Value = at.X });
        cmd.Parameters.Add(new() { Value = at.Y });
        cmd.Parameters.Add(new() { Value = skip });
        cmd.Parameters.Add(new() { Value = (object?)types ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(scope.Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AlternativeSite(new ObjectRef("site", reader.GetInt64(0), reader.GetString(1), reader.GetString(2)), Math.Round(reader.GetDouble(3)),
                reader.GetInt32(4), reader.GetInt32(5)));
        }
        return list;
    }
}

/// <summary>What a plan does to classifications and requirements (#179): raised sites, unmet requirements, suggestions and alternatives.</summary>
public sealed class PlanClassificationEndpoint(PlanClassification report) : Endpoint<PlanIdRequest, PlanClassificationReport>
{
    public override void Configure() => Get("/plans/{id}/classification");

    public override async Task HandleAsync(PlanIdRequest req, CancellationToken ct)
    {
        if (await report.ReportAsync(HttpContext.Scope(), req.Id, ct) is not { } result)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(result, ct);
    }
}

[McpServerToolType]
public sealed class PlanClassificationTools(PlanClassification report, IHttpContextAccessor http)
{
    [McpServerTool(Name = "check_plan_classification", Title = "Klassningskrav i planen", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What a draft plan does to classifications: which sites it raises to a higher criticality (a new level-5 switch makes its site " +
        "level 5), which requirements of that level are then not met (two independent cables that carry it, reserve power), operations that " +
        "would meet them, and other nearby sites that already do. Use it after adding equipment or classifications to a plan.")]
    public async Task<PlanClassificationReport> Check([Description("The plan, \"plan:12\".")] string plan, CancellationToken ct = default)
    {
        var text = plan.StartsWith("plan:", StringComparison.Ordinal) ? plan[5..] : plan;
        return long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            && await report.ReportAsync(http.HttpContext!.Scope(), id, ct) is { } result
            ? result
            : throw new McpException($"Plan {plan} finns inte, eller ligger utanför ditt omfång.");
    }
}

using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Classifications;

/// <param name="Actual">What the object has now: a count of cables, or the attribute's value (null when it is missing).</param>
/// <param name="Objects">What the actual count is made of, e.g. the cables that count.</param>
/// <param name="Hint">What would meet the requirement, in words.</param>
public sealed record RuleResult(string Rule, string Requirement, bool Met, double? Actual, int Required, IReadOnlyList<ObjectRef> Objects, string Hint);

/// <param name="Level">The object's derived level; rules from that level apply.</param>
public sealed record RuleReport(string Schema, int Level, string Name, IReadOnlyList<RuleResult> Results, int Unmet, double ElapsedMs);

/// <summary>
/// The requirements that come with an object's level (#178, ADR-0017): evaluated against production, or against a plan's view
/// (#179), where the plan's new, split and removed cables and its attribute changes count. Rules are data in the schema, and
/// there are two kinds: cables at a site, and a numeric attribute.
/// </summary>
public sealed class ClassificationRules(RequestDb db, ClassificationDerivation derivation, PlanViews views, GraphHolder holder)
{
    private sealed record SiteCable(ObjectRef Cable, long OtherSite, string OtherCode);

    /// <param name="assumeLevel">Check the requirements of this level instead of the object's derived one: "would this site meet level 5?" (#179).</param>
    public async Task<RuleReport?> EvaluateAsync(UserScope scope, string type, long id, string schemaKey, long? planId, CancellationToken ct,
        int? assumeLevel = null)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (ClassificationCatalog.Current.Find(schemaKey) is not { } schema
            || await derivation.DeriveAsync(scope, type, id, schemaKey, planId, ct) is not { } derived)
        {
            return null;
        }
        var results = new List<RuleResult>();
        var level = assumeLevel ?? derived.Level;
        var applicable = schema.RuleList.Where(r => r.AppliesTo.Contains(type) && level >= r.FromLevel).ToList();
        if (applicable.Count > 0)
        {
            var chain = planId is { } plan && holder.Current is { } production && await views.GetAsync(production, plan, scope, ct) is { } view
                ? view.Chain.Operations : [];
            List<SiteCable>? cables = null;
            Dictionary<string, JsonElement>? attributes = null;
            foreach (var rule in applicable)
            {
                if (rule.Type == "cables")
                {
                    cables ??= await CablesAtAsync(id, chain, scope, ct);
                    results.Add(await CablesAsync(rule, cables, level, scope, schemaKey, planId, ct));
                }
                else
                {
                    attributes ??= await AttributesAsync(id, chain, ct);
                    double? actual = attributes.TryGetValue(rule.Attribute!, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
                    results.Add(new RuleResult(rule.Id, rule.Requirement, actual >= rule.Min, actual, rule.Min, [],
                        $"Sätt attributet {rule.Attribute} på siten till minst {rule.Min}, till exempel med reservkraft som klarar det."));
                }
            }
        }
        return new RuleReport(schemaKey, level, schema.Level(level)?.Name ?? derived.Name, results, results.Count(r => !r.Met),
            Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
    }

    private async Task<RuleResult> CablesAsync(ClassificationRule rule, List<SiteCable> cables, int level, UserScope scope, string schemaKey, long? planId,
        CancellationToken ct)
    {
        var counted = new List<SiteCable>();
        foreach (var cable in cables)
        {
            if (rule.Supporting)
            {
                // A cable supports the level when it carries at least that level itself (#177).
                var derivedCable = await derivation.DeriveAsync(scope, "cable", cable.Cable.Id, schemaKey, planId, ct);
                if (derivedCable is null || derivedCable.Level < level)
                {
                    continue;
                }
            }
            counted.Add(cable);
        }
        var distinct = rule.Independent ? counted.GroupBy(c => c.OtherSite).Select(g => g.First()).ToList() : counted;
        var met = distinct.Count >= rule.Min;
        var missing = Math.Max(rule.Min - distinct.Count, 0);
        var hint = met ? "" : cables.Count > counted.Count
            ? $"{cables.Count - counted.Count} kablar går in men bär inte nivån. Höj dem genom att låta tjänster på nivån gå över dem, eller dra {missing} ny kabel till en annan site."
            : $"Dra {missing} kabel{(missing == 1 ? "" : "ar")} till {(rule.Independent ? "en annan site" : "siten")} som bär nivån.";
        return new RuleResult(rule.Id, rule.Requirement, met, distinct.Count, rule.Min, [.. distinct.Select(c => c.Cable)], hint);
    }

    /// <summary>In-service cables ending at the site, with what the plan adds and takes away.</summary>
    private async Task<List<SiteCable>> CablesAtAsync(long site, IReadOnlyList<PlanOp> chain, UserScope scope, CancellationToken ct)
    {
        var removed = new HashSet<long>();
        foreach (var op in chain)
        {
            if (op.Kind == "split_cable")
            {
                removed.Add(op.Payload.GetProperty("cable").GetInt64());
            }
            else if (op.Kind == "remove")
            {
                removed.UnionWith(ObjectRemoval.Objects(op.Payload).Cables);
            }
            else if (op.Kind == "move" && op.ObjectType == "cable")
            {
                removed.Add(op.ObjectId);
            }
        }
        var list = new List<SiteCable>();
        await using (var cmd = db.Source.CreateCommand($"""
            SELECT c.id, c.code, o.id, o.code FROM cable c
            JOIN site o ON o.id = CASE WHEN c.a_site_id = $1 THEN c.b_site_id ELSE c.a_site_id END
            WHERE (c.a_site_id = $1 OR c.b_site_id = $1) AND c.lifecycle = 'in_service' AND {ScopeSql.Cable("c.id", 2)}
            """))
        {
            cmd.Parameters.Add(new() { Value = site });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!removed.Contains(reader.GetInt64(0)))
                {
                    list.Add(new SiteCable(new ObjectRef("cable", reader.GetInt64(0), reader.GetString(1)), reader.GetInt64(2), reader.GetString(3)));
                }
            }
        }

        // What the plan builds: new cables, and the two parts of a split cable.
        var planned = new List<(long Id, string Code, long A, long B)>();
        foreach (var op in chain)
        {
            switch (op.Kind)
            {
                case "create_cable":
                    planned.Add((Planned.ObjectId(op.Id), $"NY-K{op.Id}", op.Payload.GetProperty("a").GetInt64(), op.Payload.GetProperty("b").GetInt64()));
                    break;
                case "move" when op.ObjectType == "cable":
                    {
                        var (moved, other) = (op.Payload.GetProperty("site").GetInt64(), op.Payload.GetProperty("otherSite").GetInt64());
                        var atA = op.Payload.GetProperty("end").GetString() == "A";
                        planned.Add((op.ObjectId, op.Payload.GetProperty("code").GetString()!, atA ? moved : other, atA ? other : moved));
                        break;
                    }
                case "split_cable":
                    {
                        var code = op.Payload.GetProperty("code").GetString()!;
                        var (a, middle, b) = (op.Payload.GetProperty("aSite").GetInt64(), op.Payload.GetProperty("site").GetInt64(), op.Payload.GetProperty("bSite").GetInt64());
                        planned.Add((Planned.ObjectId(op.Id), $"{code}-A", a, middle));
                        planned.Add((CableSplit.SecondCable(op.Id), $"{code}-B", middle, b));
                        break;
                    }
                default:
                    break;
            }
        }
        var siteCodes = new Dictionary<long, string>();
        foreach (var op in chain.Where(o => o.Kind == "create_site"))
        {
            siteCodes[Planned.ObjectId(op.Id)] = op.Payload.GetProperty("code").GetString()!;
        }
        var others = planned.Where(p => p.A == site || p.B == site).Select(p => p.A == site ? p.B : p.A).Where(o => o > 0 && !siteCodes.ContainsKey(o)).Distinct().ToArray();
        if (others.Length > 0)
        {
            await using var cmd = db.Source.CreateCommand("SELECT id, code FROM site WHERE id = ANY($1)");
            cmd.Parameters.Add(new() { Value = others });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                siteCodes[reader.GetInt64(0)] = reader.GetString(1);
            }
        }
        foreach (var (id, code, a, b) in planned.Where(p => p.A == site || p.B == site))
        {
            var other = a == site ? b : a;
            list.Add(new SiteCable(new ObjectRef("cable", id, code, null, "planned"), other, siteCodes.GetValueOrDefault(other, $"#{other}")));
        }
        return list;
    }

    /// <summary>The site's attributes with the plan's changes applied in order.</summary>
    private async Task<Dictionary<string, JsonElement>> AttributesAsync(long site, IReadOnlyList<PlanOp> chain, CancellationToken ct)
    {
        var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using (var cmd = db.Source.CreateCommand("SELECT attributes::text FROM site WHERE id = $1"))
        {
            cmd.Parameters.Add(new() { Value = site });
            if (await cmd.ExecuteScalarAsync(ct) is string json)
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    attributes[p.Name] = p.Value.Clone();
                }
            }
        }
        foreach (var op in chain.Where(o => o.Kind == "set_attributes" && o.ObjectType == "site" && o.ObjectId == site))
        {
            foreach (var p in op.Payload.GetProperty("attributes").EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Null)
                {
                    attributes.Remove(p.Name);
                }
                else
                {
                    attributes[p.Name] = p.Value.Clone();
                }
            }
        }
        return attributes;
    }
}

/// <summary>The requirements of an object's level, checked (#178): what is met and what is not, with what would meet it.</summary>
public sealed class ClassificationRulesEndpoint(RequestDb db, ClassificationRules rules) : Endpoint<DerivedClassificationRequest, RuleReport>
{
    public override void Configure() => Get("/classifications/rules");

    public override async Task HandleAsync(DerivedClassificationRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (!ClassificationCatalog.ObjectTypes.Contains(req.Type) || !await PlanSql.ObjectVisibleAsync(db.Source, req.Type, req.Id, scope, ct)
            || await rules.EvaluateAsync(scope, req.Type, req.Id, req.Schema, req.Plan, ct) is not { } report)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(report, ct);
    }
}

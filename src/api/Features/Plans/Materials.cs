using System.Diagnostics;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Plans;

/// <summary>One line of a plan's materials (#291).</summary>
/// <param name="Kind">cable (metres), equipment (units) or construction (metres of route segment built or used).</param>
/// <param name="UnitCost">The catalog's price per metre or per unit; null when the catalog has none.</param>
public sealed record MaterialLine(string Kind, string Key, string Name, decimal Quantity, string Unit, decimal? UnitCost, decimal? Cost);

/// <summary>What a plan needs and costs (#291), worked out from its changes and the catalog's prices.</summary>
/// <param name="Missing">Keys with no price in the catalog: their lines have no cost, and the total leaves them out.</param>
public sealed record PlanMaterials(string Currency, IReadOnlyList<MaterialLine> Lines, decimal Total, IReadOnlyList<string> Missing, double ElapsedMs);

/// <summary>
/// A plan's materials (#291): the cables it creates by type and length (as drawn, #259, or straight between their sites),
/// the equipment by model, the new route segments by construction, and the metres pulled through existing conduit, each
/// priced from the catalog. Only the plan's own changes, not those of the plans it builds on. Nothing is stored.
/// </summary>
public sealed class Materials(RequestDb db)
{
    private static readonly Dictionary<string, string> ConstructionNames = new()
    {
        ["trench"] = "Ny trasé, grävd",
        ["plough"] = "Ny trasé, plöjd",
        ["aerial"] = "Ny trasé, luft",
        ["existing"] = "Indragning i befintlig kanalisation",
    };

    public async Task<PlanMaterials> ForAsync(PlanView view, long planId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var catalog = TypeCatalog.Current;
        var ops = view.Chain.Operations.Where(o => o.PlanId == planId).ToList();
        var cableMetres = new Dictionary<string, decimal>();
        var units = new Dictionary<string, decimal>();
        var construction = new Dictionary<string, decimal>();

        // Straight cables need their sites' positions: production ones from the database, planned ones from the chain.
        var ends = ops.Where(o => o.Kind == "create_cable" && !o.Payload.TryGetProperty("lengthM", out _))
            .SelectMany(o => new[] { o.Payload.GetProperty("a").GetInt64(), o.Payload.GetProperty("b").GetInt64() }).ToHashSet();
        var positions = (await PlanSql.SitePositionsAsync(db, [.. ends.Where(id => id > 0)], ct)).ToDictionary(s => s.Id, s => (s.X, s.Y));
        foreach (var site in view.Chain.Operations.Where(o => o.Kind == "create_site"))
        {
            positions[Planned.ObjectId(site.Id)] = (site.Payload.GetProperty("x").GetDouble(), site.Payload.GetProperty("y").GetDouble());
        }

        foreach (var op in ops)
        {
            var p = op.Payload;
            switch (op.Kind)
            {
                case "create_cable":
                    {
                        var key = p.GetProperty("typeKey").GetString()!;
                        decimal metres;
                        if (p.TryGetProperty("lengthM", out var drawn))
                        {
                            metres = drawn.GetDecimal();
                            Add(construction, "existing", InConduit(p));
                            foreach (var segment in p.TryGetProperty("newSegments", out var news) ? news.EnumerateArray() : default)
                            {
                                Add(construction, segment.GetProperty("construction").GetString()!, LineLength(segment.GetProperty("line")));
                            }
                        }
                        else
                        {
                            metres = positions.TryGetValue(p.GetProperty("a").GetInt64(), out var a) && positions.TryGetValue(p.GetProperty("b").GetInt64(), out var b)
                                ? (decimal)Math.Round(Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y))))
                                : 0;
                        }
                        Add(cableMetres, key, metres);
                        break;
                    }
                case "create_equipment":
                    Add(units, p.GetProperty("typeKey").GetString()!, 1);
                    break;
            }
        }

        var lines = new List<MaterialLine>();
        var missing = new List<string>();
        MaterialLine Line(string kind, string key, string name, decimal quantity, string unit, decimal? price)
        {
            if (price is null)
            {
                missing.Add(key);
            }
            return new MaterialLine(kind, key, name, quantity, unit, price, price is { } each ? Math.Round(each * quantity, 2) : null);
        }
        foreach (var (key, metres) in cableMetres.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var type = catalog.FindCable(key);
            lines.Add(Line("cable", key, type?.Name ?? key, metres, "m", type?.CostPerMeter));
        }
        foreach (var (key, count) in units.OrderBy(u => u.Key, StringComparer.Ordinal))
        {
            var type = catalog.Find(key);
            lines.Add(Line("equipment", key, type is null ? key : $"{type.Manufacturer} {type.Model}", count, "st", type?.Cost));
        }
        foreach (var (key, metres) in construction.Where(c => c.Value > 0).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            lines.Add(Line("construction", key, ConstructionNames.GetValueOrDefault(key, key), metres, "m",
                catalog.Costs.Construction.TryGetValue(key, out var price) ? price : null));
        }
        return new PlanMaterials(catalog.Costs.Currency, lines, lines.Sum(l => l.Cost ?? 0), missing, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
    }

    private static void Add(Dictionary<string, decimal> totals, string key, decimal amount) =>
        totals[key] = totals.GetValueOrDefault(key) + amount;

    /// <summary>A drawn cable's metres in existing conduit: its length less its new stretches.</summary>
    private static decimal InConduit(JsonElement p)
    {
        var fresh = p.TryGetProperty("newSegments", out var news) ? news.EnumerateArray().Sum(s => LineLength(s.GetProperty("line"))) : 0;
        return Math.Max(0, p.GetProperty("lengthM").GetDecimal() - fresh);
    }

    private static decimal LineLength(JsonElement line)
    {
        var points = line.EnumerateArray().Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToList();
        var length = points.Zip(points.Skip(1), (a, b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)))).Sum();
        return (decimal)Math.Round(length);
    }
}

/// <summary>A plan's materials and costs (#291), for its workspace and for export.</summary>
public sealed class PlanMaterialsEndpoint(GraphHolder holder, PlanViews views, Materials materials) : Endpoint<PlanIdRequest, PlanMaterials>
{
    public override void Configure() => Get("/plans/{id}/materials");

    public override async Task HandleAsync(PlanIdRequest req, CancellationToken ct)
    {
        if (await views.GetAsync(holder.Require(), req.Id, HttpContext.Scope(), ct) is not { } view)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(await materials.ForAsync(view, req.Id, ct), ct);
    }
}

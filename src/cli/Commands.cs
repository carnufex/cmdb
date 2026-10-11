using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cmdb.Cli;

/// <summary>The commands (#86), mirroring the MCP tools: each calls one API route and prints compact text or its JSON.</summary>
internal static class Commands
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task RunAsync(Arguments args, Api api, TextWriter output, CancellationToken ct)
    {
        var (json, text) = args.Command switch
        {
            "search" => await SearchAsync(args, api, ct),
            "get" => await GetAsync(args, api, ct),
            "find-sites" => await FindSitesAsync(args, api, ct),
            "impact" => await ImpactAsync(args, api, ct),
            "trace" => await TraceAsync(args, api, ct),
            "neighbourhood" or "neighborhood" => await NeighbourhoodAsync(args, api, ct),
            "catalog" => await CatalogAsync(api, ct),
            "plans" => await PlansAsync(api, ct),
            "plan" => await PlanAsync(args, api, ct),
            "whoami" => await WhoAmIAsync(api, ct),
            "reconciliations" => await ReconciliationsAsync(api, ct),
            "reconciliation" => await ReconciliationAsync(args, api, ct),
            _ => throw new CliException(ExitCodes.Usage, $"Okänt kommando: {args.Command}. Se cmdb help."),
        };
        await output.WriteAsync(args.Json ? json.ToJsonString(Indented) + Environment.NewLine : text);
    }

    private static async Task<(JsonNode, string)> SearchAsync(Arguments args, Api api, CancellationToken ct)
    {
        var q = string.Join(' ', args.Positional);
        if (q.Length == 0)
        {
            throw new CliException(ExitCodes.Usage, "Ange vad du söker efter: cmdb search RAD-000123");
        }
        var hits = await api.GetAsync($"/api/search?q={Uri.EscapeDataString(q)}&limit={args.Number("limit", 20, 1, 50)}", ct);
        var text = new StringBuilder();
        foreach (var hit in hits.AsArray())
        {
            var type = Text.Get(hit, "type");
            Text.Line(text, $"{type}:{Text.Id(hit)}", Text.Get(hit, "code"), Text.Get(hit, "name"), Paren(Text.Get(hit, "detail"), Text.Get(hit, "lifecycle")),
                api.Link(type, Text.Id(hit)));
        }
        return (hits, text.Length == 0 ? "Inga träffar.\n" : text.ToString());
    }

    private static async Task<(JsonNode, string)> GetAsync(Arguments args, Api api, CancellationToken ct)
    {
        var target = await ResolveAsync(args.Required(0, "ett objekt, till exempel site:12 eller en kod"), api, ct);
        var detail = await api.GetAsync($"/api/{target.Path}/{target.Id}", ct);
        var text = new StringBuilder();
        Text.Line(text, target.ToString(), Text.Get(detail, "code").Length > 0 ? Text.Get(detail, "code") : Text.Get(detail, "name"),
            Text.Get(detail, "code").Length > 0 ? Text.Get(detail, "name") : null, Paren(Text.Get(detail, "lifecycle")), api.Link(target.Type, target.Id));
        foreach (var (key, value) in detail.AsObject())
        {
            switch (value)
            {
                case JsonValue v when key is not ("id" or "code" or "name" or "lifecycle"):
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {key}: {v}");
                    break;
                case JsonObject o when o["type"] is not null && o["code"] is not null:
                    text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {key}: {o["type"]}:{o["id"]} {o["code"]} {o["name"]}").TrimEnd());
                    break;
                case JsonObject o when key == "attributes" && o.Count > 0:
                    var attributes = string.Join(", ", o.Select(p => $"{p.Key}={p.Value}"));
                    text.AppendLine(CultureInfo.InvariantCulture, $"  attribut: {attributes}");
                    break;
                case JsonArray a when a.Count > 0:
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {key}: {a.Count}");
                    break;
                default:
                    break;
            }
        }
        return (detail, text.ToString());
    }

    private static async Task<(JsonNode, string)> FindSitesAsync(Arguments args, Api api, CancellationToken ct)
    {
        var equipment = args.All("model").Select(m => new { typeKey = m }).Cast<object>()
            .Concat(args.All("category").Select(c => new { category = c })).ToList();
        var body = new
        {
            siteTypes = args.All("type"),
            lifecycles = args.All("lifecycle"),
            serviceTypes = args.All("service-type"),
            equipment,
            limit = args.Number("limit", 25, 1, 200),
        };
        var result = await api.PostAsync("/api/query/sites", body, ct);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{result["total"]} siter");
        foreach (var site in result["sites"]!.AsArray())
        {
            Text.Line(text, $"site:{Text.Id(site)}", Text.Get(site, "code"), Text.Get(site, "name"), Paren(Text.Get(site, "siteType"), Text.Get(site, "lifecycle")),
                site!["matching"] is JsonValue m ? $"{m} matchande" : null, api.Link("site", Text.Id(site)));
        }
        return (result, text.ToString());
    }

    private static async Task<(JsonNode, string)> ImpactAsync(Arguments args, Api api, CancellationToken ct)
    {
        var target = await ResolveAsync(args.Required(0, "en kabel, utrustning eller site"), api, ct);
        if (target.Type is not ("cable" or "equipment" or "site"))
        {
            throw new CliException(ExitCodes.Usage, "impact gäller kablar, utrustning och siter.");
        }
        var impact = await api.GetAsync($"/api/{target.Path}/{target.Id}/impact", ct);
        var text = new StringBuilder();
        var hidden = impact["hiddenServices"]?.GetValue<int>() ?? 0;
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{target}: {impact["circuits"]} kretsar ({impact["direct"]} direkt), {impact["services"]!.AsArray().Count} tjänster{(hidden > 0 ? $" och {hidden} utanför ditt omfång" : "")}");
        foreach (var s in impact["services"]!.AsArray())
        {
            var service = s!["service"];
            var via = string.Join(" < ", s["path"]!.AsArray().Select(p => Text.Get(p!["circuit"], "code")));
            Text.Line(text, $"  service:{Text.Id(service)}", Text.Get(service, "code"), Text.Get(service, "name"), $"via {via}");
        }
        return (impact, text.ToString());
    }

    private static async Task<(JsonNode, string)> TraceAsync(Arguments args, Api api, CancellationToken ct)
    {
        var raw = args.Required(0, "service:ID, circuit:ID eller terminal:ID");
        var target = Ref.TryParse(raw, out var r) ? r : await ResolveAsync(raw, api, ct);
        if (target.Type is not ("service" or "circuit" or "terminal"))
        {
            throw new CliException(ExitCodes.Usage, "trace gäller service:ID, circuit:ID eller terminal:ID.");
        }
        var trace = await api.GetAsync($"/api/trace?{target.Type}={target.Id}", ct);
        var text = new StringBuilder();
        if (trace["service"] is JsonObject service)
        {
            Text.Line(text, $"service:{Text.Id(service)}", Text.Get(service, "code"), Text.Get(service, "name"));
        }
        foreach (var c in trace["circuits"]?.AsArray() ?? [])
        {
            var indent = new string(' ', 2 * ((c!["depth"]?.GetValue<int>() ?? 0) + 1));
            text.AppendLine(CultureInfo.InvariantCulture, $"{indent}{Text.Get(c["circuit"], "code")} ({Text.Get(c, "layer")}, {c["hops"]!.AsArray().Count} hopp)");
        }
        if (trace["physical"] is JsonObject physical)
        {
            text.AppendLine("Fysisk väg:");
            foreach (var hop in physical["hops"]!.AsArray())
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {(Text.Get(hop, "edge") is { Length: > 0 } e ? e + " → " : "")}{Text.Get(hop, "label")}");
            }
            text.AppendLine(CultureInfo.InvariantCulture, $"  slut: {string.Join(", ", physical["ends"]!.AsArray().Select(x => x!.ToString()))}");
        }
        return (trace, text.Length == 0 ? "Inget att spåra.\n" : text.ToString());
    }

    private static async Task<(JsonNode, string)> NeighbourhoodAsync(Arguments args, Api api, CancellationToken ct)
    {
        var target = await ResolveAsync(args.Required(0, "en site"), api, ct);
        if (target.Type != "site")
        {
            throw new CliException(ExitCodes.Usage, "neighbourhood gäller siter.");
        }
        var sites = await api.GetAsync($"/api/sites/{target.Id}/neighbourhood?hops={args.Number("hops", 1, 1, 3)}", ct);
        var text = new StringBuilder();
        foreach (var site in sites.AsArray())
        {
            Text.Line(text, $"site:{Text.Id(site)}", Text.Get(site, "code"), Text.Get(site, "name"), $"{site!["hops"]} hopp",
                Text.Get(site, "via") is { Length: > 0 } via ? $"via {via}" : null);
        }
        return (sites, text.Length == 0 ? "Inga grannar.\n" : text.ToString());
    }

    private static async Task<(JsonNode, string)> CatalogAsync(Api api, CancellationToken ct)
    {
        var fields = await api.GetAsync("/api/query/fields", ct);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Sitetyper: {string.Join(", ", fields["siteTypes"]!.AsArray())}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Livscykler: {string.Join(", ", fields["lifecycles"]!.AsArray())}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Tjänstetyper: {string.Join(", ", fields["serviceTypes"]!.AsArray())}");
        text.AppendLine("Modeller:");
        foreach (var t in fields["types"]!.AsArray())
        {
            Text.Line(text, $"  {Text.Get(t, "key")}", $"{Text.Get(t, "manufacturer")} {Text.Get(t, "model")}", Paren(Text.Get(t, "category")));
        }
        return (fields, text.ToString());
    }

    private static async Task<(JsonNode, string)> PlansAsync(Api api, CancellationToken ct)
    {
        var plans = await api.GetAsync("/api/plans", ct);
        var text = new StringBuilder();
        foreach (var p in plans.AsArray())
        {
            Text.Line(text, $"plan:{Text.Id(p)}", Text.Get(p, "name"), Paren(Text.Get(p, "status"), $"{p!["operations"]} ändringar"),
                p["conflicts"]?.GetValue<int>() is > 0 and var c ? $"{c} konflikter" : null, Text.Get(p, "flag"),
                new Uri(api.BaseUrl, $"/?plan={Text.Id(p)}").ToString());
        }
        return (plans, text.Length == 0 ? "Inga planer.\n" : text.ToString());
    }

    private static async Task<(JsonNode, string)> PlanAsync(Arguments args, Api api, CancellationToken ct)
    {
        var raw = args.Required(0, "en plan, plan:12");
        var id = Ref.TryParse(raw, out var r) && r.Type == "plan" ? r.Id
            : long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n
            : throw new CliException(ExitCodes.Usage, "Ange en plan som plan:12.");
        var view = await api.GetAsync($"/api/plans/{id}/view", ct);
        var text = new StringBuilder();
        Text.Line(text, $"plan:{id}", Text.Get(view["plan"], "name"), Paren(Text.Get(view["plan"], "status")),
            $"{view["changes"]!.AsArray().Count} ändringar, {view["problems"]} problem", new Uri(api.BaseUrl, $"/?plan={id}").ToString());
        foreach (var change in view["changes"]!.AsArray())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {Text.Get(change, "summary")}");
            if (Text.Get(change, "problem") is { Length: > 0 } problem)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    ! {problem}");
            }
            foreach (var conflict in change!["conflicts"]?.AsArray() ?? [])
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    ! {conflict}");
            }
        }
        return (view, text.ToString());
    }

    private static async Task<(JsonNode, string)> WhoAmIAsync(Api api, CancellationToken ct)
    {
        var me = await api.GetAsync("/api/me", ct);
        var scopes = me["scopes"]?.AsArray().Select(s => s!.ToString()).ToList() ?? [];
        return (me, $"{Text.Get(me, "name")} ({Text.Get(me, "username")}) via {Text.Get(me, "client")}\n"
            + $"Omfång: {(scopes.Count == 0 ? "inget" : string.Join(", ", scopes))}\n");
    }

    /// <summary>A reference as given, or an exact code found with the quick search.</summary>
    private static async Task<(JsonNode, string)> ReconciliationsAsync(Api api, CancellationToken ct)
    {
        var runs = await api.GetAsync("/api/reconciliations", ct);
        return (runs, Sync.ReconciliationText.List(runs));
    }

    private static async Task<(JsonNode, string)> ReconciliationAsync(Arguments args, Api api, CancellationToken ct)
    {
        var text = args.Required(0, "en avstämning, till exempel 12 eller reconciliation:12");
        if (!long.TryParse(text.StartsWith("reconciliation:", StringComparison.Ordinal) ? text["reconciliation:".Length..] : text,
                NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            throw new CliException(ExitCodes.Usage, $"Ingen avstämning: {text}.");
        }
        var report = await api.GetAsync($"/api/reconciliations/{id}", ct);
        return (report, Sync.ReconciliationText.Report(report));
    }

    private static async Task<Ref> ResolveAsync(string text, Api api, CancellationToken ct)
    {
        if (Ref.TryParse(text, out var r))
        {
            return r;
        }
        var hits = await api.GetAsync($"/api/search?q={Uri.EscapeDataString(text)}&limit=10", ct);
        var exact = hits.AsArray().FirstOrDefault(h => string.Equals(Text.Get(h, "code"), text, StringComparison.OrdinalIgnoreCase));
        return exact is null
            ? throw new CliException(ExitCodes.NotFound, $"Hittar inget med koden {text}. Prova cmdb search {text}.")
            : new Ref(Text.Get(exact, "type"), Text.Id(exact));
    }

    private static string? Paren(params string[] parts)
    {
        var present = parts.Where(p => p.Length > 0).ToList();
        return present.Count == 0 ? null : $"({string.Join(", ", present)})";
    }
}

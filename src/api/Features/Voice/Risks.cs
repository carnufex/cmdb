using System.Globalization;
using Cmdb.Api.Auth;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Voice;

public sealed record RiskService(string Code, string Name, bool Critical);

/// <param name="Id">Stable while the cause stays: "dig-{work}-{cable}", "redundancy-{service}" or "battery-{equipment}".</param>
/// <param name="Reference">What an incident for the risk is about: "cable:ID" or "site:ID".</param>
public sealed record Risk(string Id, string Kind, string Title, string Description, string Reference, long SiteId, string SiteCode, string SiteName,
    int AffectedServices, int CriticalServices, IReadOnlyList<RiskService> TopServices, string ResponsibleEmployeeId, string ResponsibleName,
    string SuggestedAction);

/// <summary>
/// Risks the proactive agent finds before they become faults (#137): planned work across a cable route, false
/// redundancy (every path of a service through one site), and backup batteries past their lifetime. Within the
/// caller's access scopes (#22), like everything else.
/// </summary>
public static class RiskDetection
{
    public const int BatteryLifetimeYears = 8;

    public static async Task<IReadOnlyList<Risk>> RunAsync(Cmdb.Graph.Graph g, GraphMask mask, NpgsqlDataSource db, UserScope scope, CancellationToken ct)
    {
        var (defaultId, defaultName) = await ResponsibleAsync(db, ct);
        var risks = new List<Risk>();
        risks.AddRange(await DiggingAsync(g, mask, db, scope, ct));
        risks.AddRange(await FalseRedundancyAsync(g, mask, db, defaultId, defaultName, ct));
        risks.AddRange(await BatteriesAsync(g, mask, db, scope, defaultId, defaultName, ct));
        return risks;
    }

    private static async Task<List<Risk>> DiggingAsync(Cmdb.Graph.Graph g, GraphMask mask, NpgsqlDataSource db, UserScope scope, CancellationToken ct)
    {
        var rows = new List<(long Work, string Title, string Description, string Contractor, string Responsible, string? Name, DateTimeOffset From,
            DateTimeOffset To, long Cable, string CableCode)>();
        await using (var cmd = db.CreateCommand($"""
            SELECT w.id, w.title, w.description, w.contractor, w.responsible_employee_id, v.name, w.starts_at, w.ends_at, c.id, c.code
            FROM planned_work w
            JOIN cable c ON c.geom && w.area AND ST_Intersects(c.geom, w.area) AND c.lifecycle <> 'removed'
            LEFT JOIN voice_caller v ON v.employee_id = w.responsible_employee_id
            WHERE w.ends_at > now() AND {ScopeSql.Cable("c.id", 1)}
            ORDER BY w.starts_at, c.id
            """))
        {
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetInt64(8), reader.GetString(9)));
            }
        }
        var risks = new List<Risk>();
        foreach (var r in rows)
        {
            if (await FaultAnalysis.RunAsync(g, mask, db, "cable", r.Cable, ct) is not { } fault)
            {
                continue;
            }
            var critical = fault.Services.Count(s => s.Critical);
            risks.Add(new Risk($"dig-{r.Work}-{r.Cable}", "digging", $"{r.Title} korsar kabel {r.CableCode}",
                $"{r.Contractor} gräver {Day(r.From)}–{Day(r.To)} där kabel {r.CableCode} går, nära {fault.SiteName}. " +
                $"Kabeln bär {fault.Affected} tjänster, varav {critical} kritiska. {r.Description}",
                $"cable:{r.Cable}", fault.SiteId, fault.SiteCode, fault.SiteName, fault.Affected, critical, Top(fault),
                r.Responsible, r.Name ?? r.Responsible,
                "Kontakta entreprenören före start, beställ kabelutsättning och skyddsåtgärd, och bekräfta att reservvägar är i drift."));
        }
        return risks;
    }

    private static async Task<List<Risk>> FalseRedundancyAsync(Cmdb.Graph.Graph g, GraphMask mask, NpgsqlDataSource db, string responsible, string name,
        CancellationToken ct)
    {
        var found = new List<(int Service, List<int> Sites)>();
        for (var s = 0; s < g.ServiceCount; s++)
        {
            var paths = g.CircuitsOf(s);
            if (paths.Length < 2 || !mask.ServiceVisible(s))
            {
                continue;
            }
            HashSet<int>? shared = null;
            foreach (var c in paths)
            {
                var sites = Footprint(g, c);
                shared = shared is null ? sites : [.. shared.Intersect(sites)];
            }
            var visible = shared?.Where(mask.SiteVisible).Order().ToList() ?? [];
            if (visible.Count > 0)
            {
                found.Add((s, visible));
            }
        }
        var risks = new List<Risk>();
        foreach (var (service, shared) in found)
        {
            // The shared site that makes the point: an aggregation node before a hub (hubs are protected themselves).
            var sites = await SitesAsync(db, [.. shared.Select(g.SiteId)], ct);
            var (siteId, _, _) = sites.OrderBy(s => s.Type switch { "aggregation" => 0, "hub" => 1, _ => 2 }).ThenBy(s => s.Id).First();
            if (await FaultAnalysis.RunAsync(g, mask, db, "site", siteId, ct) is not { } fault)
            {
                continue;
            }
            var serviceId = g.ServiceId(service);
            var own = fault.Services.FirstOrDefault(x => x.Id == serviceId);
            var label = own is null ? $"tjänst {serviceId}" : $"{own.Name} ({own.Code})";
            var all = string.Join(" och ", sites.Select(s => s.Name));
            risks.Add(new Risk($"redundancy-{serviceId}", "false-redundancy", $"Falsk redundans: {label}",
                $"{label} har {g.CircuitsOf(service).Length} vägar på papperet, men alla går via {all}. " +
                $"Ett fel på {fault.SiteName} tar ner tjänsten trots reservvägen.",
                $"site:{siteId}", siteId, fault.SiteCode, fault.SiteName, fault.Affected, fault.Services.Count(x => x.Critical), Top(fault),
                responsible, name, $"Dra om reservvägen så att den inte passerar {fault.SiteName}, eller märk tjänsten som oskyddad."));
        }
        return risks;
    }

    private static async Task<List<Risk>> BatteriesAsync(Cmdb.Graph.Graph g, GraphMask mask, NpgsqlDataSource db, UserScope scope, string responsible,
        string name, CancellationToken ct)
    {
        var rows = new List<(long Equipment, string EquipmentName, int Year, long Site)>();
        var limit = DateTimeOffset.UtcNow.Year - BatteryLifetimeYears;
        await using (var cmd = db.CreateCommand($"""
            SELECT e.id, e.name, (e.attributes->>'installationYear')::int, e.site_id
            FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id
            WHERE t.category = 'power' AND e.attributes ? 'installationYear' AND (e.attributes->>'installationYear')::int <= $2
              AND {ScopeSql.Site("e.site_id", 1)}
            ORDER BY e.id
            """))
        {
            cmd.Parameters.Add(scope.Parameter());
            cmd.Parameters.Add(new() { Value = limit });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3)));
            }
        }
        var risks = new List<Risk>();
        foreach (var r in rows)
        {
            if (await FaultAnalysis.RunAsync(g, mask, db, "site", r.Site, ct) is not { } fault)
            {
                continue;
            }
            var critical = fault.Services.Count(s => s.Critical);
            risks.Add(new Risk($"battery-{r.Equipment}", "battery", $"Gammalt reservkraftbatteri på {fault.SiteName}",
                $"Batteriet {r.EquipmentName} på {fault.SiteName} installerades {r.Year} och har passerat livslängden på {BatteryLifetimeYears} år. " +
                $"Vid strömavbrott kan reservtiden vara betydligt kortare än de 2–4 timmar som anges. Stationen bär {fault.Affected} tjänster, " +
                $"varav {critical} kritiska.",
                $"site:{r.Site}", r.Site, fault.SiteCode, fault.SiteName, fault.Affected, critical, Top(fault), responsible, name,
                "Byt batteriet och testa reservkrafttiden."));
        }
        return risks;
    }

    private static readonly string[] Months =
        ["januari", "februari", "mars", "april", "maj", "juni", "juli", "augusti", "september", "oktober", "november", "december"];

    /// <summary>"2 oktober": the API runs without cultures (invariant globalization), so Swedish months are spelled out here.</summary>
    private static string Day(DateTimeOffset at) => string.Create(CultureInfo.InvariantCulture, $"{at.Day} {Months[at.Month - 1]}");

    private static async Task<List<(long Id, string Name, string Type)>> SitesAsync(NpgsqlDataSource db, long[] ids, CancellationToken ct)
    {
        var sites = new List<(long, string, string)>();
        await using var cmd = db.CreateCommand("SELECT id, name, site_type FROM site WHERE id = ANY($1) ORDER BY id");
        cmd.Parameters.Add(new() { Value = ids });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sites.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }
        return sites;
    }

    /// <summary>The sites a circuit runs through, down through the circuits it rides on.</summary>
    private static HashSet<int> Footprint(Cmdb.Graph.Graph g, int circuit)
    {
        var sites = new HashSet<int>();
        var seen = new HashSet<int>();
        var stack = new Stack<int>([circuit]);
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (!seen.Add(c))
            {
                continue;
            }
            foreach (var node in g.HopsOf(c))
            {
                var site = g.SiteIndexOfNode(node);
                if (site >= 0)
                {
                    sites.Add(site);
                }
            }
            foreach (var carrier in g.CarriersOf(c))
            {
                stack.Push(carrier);
            }
        }
        return sites;
    }

    private static List<RiskService> Top(Fault fault) =>
        [.. fault.Services.Where(s => s.Critical).Concat(fault.Services.Where(s => !s.Critical)).Take(5)
            .Select(s => new RiskService(s.Code, s.Name ?? "", s.Critical))];

    /// <summary>The technician on call for the area in the demo: the first technician among the callers.</summary>
    private static async Task<(string Id, string Name)> ResponsibleAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT employee_id, name FROM voice_caller WHERE role = 'technician' ORDER BY employee_id LIMIT 1");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : ("", "");
    }
}

/// <summary>Risks within the caller's scopes (#137), for the web app's agent panel.</summary>
public sealed class ListRisksEndpoint(RequestDb db, GraphHolder holder, ScopeMasks masks) : EndpointWithoutRequest<IReadOnlyList<Risk>>
{
    public override void Configure() => Get("/risks");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (holder.Current is not { } graph)
        {
            await Send.OkAsync([], ct);
            return;
        }
        var scope = HttpContext.Scope();
        await Send.OkAsync(await RiskDetection.RunAsync(graph, await masks.GetAsync(graph, scope, ct), db.Source, scope, ct), ct);
    }
}

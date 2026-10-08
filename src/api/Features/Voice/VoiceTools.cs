using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Sites;
using Cmdb.Graph;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.Voice;

public sealed record StationMatch(string Station, string Code, string Name, string Type, string Region, string Status, double Confidence);

public sealed record CodeSent(string Status, string Message);

public sealed record Verification(string Status, string Message, string? Name = null, string? Role = null, int AttemptsLeft = 0);

public sealed record StationEquipment(string Name, string Model, string Category, string Status);

public sealed record StationOverview(string Station, string Code, string Name, string Type, string Status, int EquipmentCount,
    IReadOnlyList<StationEquipment> Equipment, int Cables, IReadOnlyList<string> Neighbours);

public sealed record SpokenService(string Code, string Name, string Type, bool Critical, string Redundancy, int Paths);

public sealed record FaultImpact(string Reference, string Station, string StationName, string Priority, int AffectedServices, int ServicesDown,
    int CriticalDown, int FalseRedundancy, string Summary, IReadOnlyList<SpokenService> TopServices);

/// <summary>No incident number: the agent does not read it out, the caller gets it by SMS (#145).</summary>
public sealed record IncidentCreated(string Priority, string Station, string Summary, bool OnCallNotified, bool NumberSentBySms);

/// <summary>
/// The operations agent's tools (ADR-0015, #133, #134, #135), served only on <c>/voice/mcp</c>. Answers are short and
/// speakable. Finding a station and verifying are open; everything else needs a verified caller and runs within their
/// access scopes, enforced by the server whatever the agent's prompt says.
/// </summary>
[McpServerToolType]
public sealed class VoiceTools(SystemDb system, RequestDb db, GraphHolder holder, ScopeMasks masks, IHttpContextAccessor http,
    ILogger<VoiceTools> logger, Classifications.ClassificationRules rules)
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "find_station", "request_verification_code", "verify_caller", "station_overview", "fault_impact", "create_incident", "risk_details",
    };

    private const int TopServices = 5;

    private ClaimsPrincipal User => http.HttpContext!.User;
    private string Conversation => User.FindFirstValue(VoiceClaims.Conversation) ?? "unknown";
    private string? Employee => User.FindFirstValue(VoiceClaims.Employee);
    private UserScope Scope => http.HttpContext!.Scope();

    [McpServerTool(Name = "find_station", Title = "Hitta station", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds a station (site) by what the caller said: name, code, alias or abbreviation, also misheard. Open to " +
        "unverified callers: returns only name, code, type, region and status, never equipment or topology. Confirm the " +
        "best match with the caller (\"Menar du Lingonåsen?\") before acting on it.")]
    public async Task<IReadOnlyList<StationMatch>> FindStation(
        [Description("What the caller called the station, e.g. \"Lingonåsen\", \"LGÅ\", \"agg 1191\".")] string query,
        CancellationToken ct = default)
    {
        return await AuditAsync("find_station", async () =>
        {
            var q = query.Trim();
            if (q.Length < 2)
            {
                return [];
            }
            // Deliberately public (ADR-0015): what a sign at the station says. Not scoped, and nothing more than this.
            await using var cmd = system.Source.CreateCommand("""
                WITH scored AS (
                    SELECT s.id, s.code, s.name, s.site_type, s.lifecycle::text AS lifecycle, ST_Y(s.geom) AS y,
                           greatest(similarity(lower(s.name), lower($1)), similarity(lower(s.code), lower($1)),
                                    CASE WHEN lower(s.code) LIKE '%' || lower($2) THEN 0.9 ELSE 0 END,
                                    coalesce((SELECT max(similarity(lower(a), lower($1))) FROM jsonb_array_elements_text(s.attributes->'aliases') a), 0)) AS score
                    FROM site s
                    WHERE s.lifecycle <> 'removed'
                      AND (s.name % $1 OR s.code % $1 OR ($2 <> '' AND s.code LIKE '%' || $2)
                           OR (s.attributes ? 'aliases' AND EXISTS (SELECT 1 FROM jsonb_array_elements_text(s.attributes->'aliases') a WHERE a % $1 OR lower(a) = lower($1))))
                )
                SELECT id, code, name, site_type, lifecycle, y, score FROM scored ORDER BY score DESC, code LIMIT 3
                """);
            cmd.Parameters.Add(new() { Value = q });
            // "agg 1191" or "tolv nitton" spoken as digits: match the number at the end of a code.
            var digits = new string(q.Where(char.IsAsciiDigit).ToArray());
            cmd.Parameters.Add(new() { Value = digits.Length >= 3 ? digits : "" });
            var matches = new List<StationMatch>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                matches.Add(new StationMatch($"site:{reader.GetInt64(0)}", reader.GetString(1), reader.GetString(2), SiteType(reader.GetString(3)),
                    Region(reader.IsDBNull(5) ? null : reader.GetDouble(5)), Status(reader.GetString(4)), Math.Round(reader.GetDouble(6), 2)));
            }
            return (IReadOnlyList<StationMatch>)matches;
        }, r => r.Count == 0 ? "no-match" : "ok", reference: r => r.Count > 0 ? r[0].Station : null);
    }

    [McpServerTool(Name = "request_verification_code", Title = "Skicka verifieringskod", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Step-up verification, step 1: sends a six-digit code by SMS to the phone registered for the caller's " +
        "employee id. Needed before anything beyond find_station. Ask the caller for their employee id first.")]
    public async Task<CodeSent> RequestVerificationCode(
        [Description("The caller's employee id, digits only, e.g. \"1001\".")] string employeeId,
        CancellationToken ct = default)
    {
        return await AuditAsync("request_verification_code", async () =>
            await VoiceSessions.RequestCodeAsync(system.Source, Conversation, employeeId, ct) == CodeRequest.Locked
                ? new CodeSent("locked", "Verifieringen är låst för det här samtalet efter för många försök. Erbjud att koppla till NOC.")
                : new CodeSent("sent", "Om anställningsnumret finns har en sexsiffrig kod skickats med SMS till telefonen som är registrerad på det. " +
                    "Be uppringaren läsa upp koden. Den gäller i fem minuter."),
            r => r.Status, employeeId: VoiceSessions.Normalize(employeeId));
    }

    [McpServerTool(Name = "verify_caller", Title = "Verifiera uppringare", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Step-up verification, step 2: checks the code the caller read out. On success the call is verified for " +
        "30 minutes and the other tools answer within the caller's access. Three wrong codes lock verification for the call.")]
    public async Task<Verification> VerifyCaller(
        [Description("The caller's employee id, the same as for the code.")] string employeeId,
        [Description("The six digits the caller read out.")] string code,
        CancellationToken ct = default)
    {
        return await AuditAsync("verify_caller", async () =>
        {
            var (result, left) = await VoiceSessions.VerifyAsync(system.Source, Conversation, employeeId, code, ct);
            if (result != CodeCheck.Verified)
            {
                return result switch
                {
                    CodeCheck.Wrong => new Verification("wrong", $"Fel kod. {left} försök kvar.", AttemptsLeft: left),
                    CodeCheck.Expired => new Verification("expired", "Koden har gått ut. Skicka en ny kod."),
                    CodeCheck.NoCode => new Verification("no-code", "Ingen kod har skickats för det anställningsnumret i det här samtalet."),
                    _ => new Verification("locked", "Tre felaktiga försök. Verifieringen är låst för samtalet. Erbjud att koppla till NOC."),
                };
            }
            var caller = await VoiceSessions.CallerAsync(system.Source, employeeId, ct);
            return new Verification("verified", $"Verifierad som {caller!.Name} ({Role(caller.Role)}). Behörighet enligt hens åtkomstomfång i CMDB:n.",
                caller.Name, Role(caller.Role));
        }, r => r.Status, employeeId: VoiceSessions.Normalize(employeeId));
    }

    [McpServerTool(Name = "station_overview", Title = "Stationsöversikt", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A verified caller's view of a station: equipment and its status, cables and neighbouring stations. " +
        "Within the caller's access; a station outside it answers as not found.")]
    public async Task<StationOverview> StationOverview(
        [Description("The station as find_station returned it, e.g. \"site:1191\".")] string station,
        CancellationToken ct = default)
    {
        return await AuditAsync("station_overview", async () =>
        {
            RequireVerified();
            var id = SiteId(station);
            var detail = await GetSiteEndpoint.LoadAsync(db.Source, id, Scope, ct)
                ?? throw new McpException("Stationen finns inte, eller ligger utanför uppringarens behörighet.");
            var equipment = detail.Locations.SelectMany(l => l.Equipment).ToList();
            return new StationOverview($"site:{id}", detail.Code, detail.Name, SiteType(detail.SiteType), Status(detail.Lifecycle), equipment.Count,
                [.. equipment.OrderByDescending(e => e.Ports).Take(8).Select(e => new StationEquipment(e.Name, e.Model, e.Category, Status(e.Lifecycle)))],
                detail.Cables.Count,
                [.. detail.Cables.Select(c => $"{c.OtherEnd.Code} {c.OtherEnd.Name}".Trim()).Distinct().Take(6)]);
        }, reference: r => r.Station);
    }

    [McpServerTool(Name = "fault_impact", Title = "Felpåverkan", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What a fault at a station (or on one piece of equipment) takes down: affected services, which are critical, " +
        "and for each whether another path still works, every path runs through the fault (false redundancy), or it " +
        "has only one path. Gives the priority an incident would get. Verified callers only.")]
    public async Task<FaultImpact> FaultImpact(
        [Description("\"site:ID\" from find_station, or \"equipment:ID\".")] string reference,
        CancellationToken ct = default)
    {
        return await AuditAsync("fault_impact", async () =>
        {
            RequireVerified();
            var fault = await AnalyseAsync(reference, ct);
            return new FaultImpact(fault.Reference, $"site:{fault.SiteId}", fault.SiteName, fault.Priority, fault.Affected, fault.Down, fault.CriticalDown,
                fault.FalseRedundancy, Summary(fault), [.. fault.Services.Take(TopServices).Select(Spoken)]);
        }, r => r.Priority, reference: r => r.Reference);
    }

    [McpServerTool(Name = "create_incident", Title = "Skapa ärende", ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates an incident for the reported fault. The server enriches it with the impact (services, redundancy) " +
        "and sets the priority by rules; P1 notifies the on-call engineer. The incident number is sent to the caller by SMS, " +
        "not returned here. Verified callers only; confirm the station with the caller first.")]
    public async Task<IncidentCreated> CreateIncident(
        [Description("\"site:ID\" or \"equipment:ID\" where the fault is.")] string reference,
        [Description("What is wrong, in one sentence, e.g. \"Ingen länk på Lingonåsen sedan 14.10\".")] string description,
        [Description("What the caller has seen or done: alarms, LEDs, power, what they tried. Empty if nothing.")] string observations,
        CancellationToken ct = default)
    {
        string? about = null;
        return await AuditAsync("create_incident", async () =>
        {
            RequireVerified();
            var fault = await AnalyseAsync(reference, ct);
            about = fault.Reference;
            var enrichment = JsonSerializer.SerializeToDocument(new
            {
                fault.Affected,
                fault.Down,
                fault.CriticalDown,
                fault.FalseRedundancy,
                summary = Summary(fault),
                services = fault.Services.Take(20).Select(Spoken),
            }, JsonSerializerOptions.Web);
            await using var cmd = db.Source.CreateCommand("""
                INSERT INTO incident (site_id, reference, description, observations, priority, conversation_id, reported_by, enrichment)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING id
                """);
            cmd.Parameters.Add(new() { Value = fault.SiteId });
            cmd.Parameters.Add(new() { Value = fault.Reference });
            cmd.Parameters.Add(new() { Value = Clip(description, 500) });
            cmd.Parameters.Add(new() { Value = Clip(observations, 2000) });
            cmd.Parameters.Add(new() { Value = fault.Priority });
            cmd.Parameters.Add(new() { Value = Conversation });
            cmd.Parameters.Add(new() { Value = $"{User.Identity?.Name} ({Employee})" });
            cmd.Parameters.Add(new() { Value = enrichment, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            var number = Incidents.Number(id);
            var notified = fault.Priority == "P1";
            if (notified)
            {
                VoiceLog.OnCall(logger, number, fault.SiteCode, fault.CriticalDown);
            }
            var caller = await VoiceSessions.CallerAsync(db.Source, Employee!, ct);
            if (caller is not null)
            {
                await VoiceSessions.SmsAsync(db.Source, caller,
                    $"Driftagenten: ärende {number} ({fault.Priority}) för {fault.SiteName} är skapat.{(notified ? " Jouren är larmad." : "")}", ct);
            }
            return new IncidentCreated(fault.Priority, fault.SiteName, Summary(fault), notified, caller is not null);
        }, r => r.Priority, reference: _ => about);
    }

    [McpServerTool(Name = "risk_details", Title = "Riskdetaljer", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("For a proactive call about a risk (#137): what the risk is, where, which services it threatens and the " +
        "suggested action. The risk id comes from the call's context (risk_id). Verified callers only; an incident for " +
        "the risk is created with create_incident on the risk's reference.")]
    public async Task<Risk> RiskDetails(
        [Description("The risk id from the call's context, e.g. \"dig-1-4711\".")] string riskId,
        CancellationToken ct = default)
    {
        return await AuditAsync("risk_details", async () =>
        {
            RequireVerified();
            if (holder.Current is not { } graph)
            {
                throw new McpException("Nätgrafen laddas, försök igen om några sekunder.");
            }
            var risks = await RiskDetection.RunAsync(graph, await masks.GetAsync(graph, Scope, ct), db.Source, Scope, ct, rules);
            return risks.FirstOrDefault(r => r.Id == riskId.Trim())
                ?? throw new McpException("Risken finns inte längre, eller ligger utanför uppringarens behörighet.");
        }, r => r.Kind, reference: r => r.Reference);
    }

    private async Task<Fault> AnalyseAsync(string reference, CancellationToken ct)
    {
        var (type, id) = Reference(reference);
        if (holder.Current is not { } graph)
        {
            throw new McpException("Nätgrafen laddas, försök igen om några sekunder.");
        }
        return await FaultAnalysis.RunAsync(graph, await masks.GetAsync(graph, Scope, ct), db.Source, type, id, ct)
            ?? throw new McpException("Stationen eller utrustningen finns inte, eller ligger utanför uppringarens behörighet.");
    }

    private void RequireVerified()
    {
        if (Employee is null)
        {
            throw new McpException("Uppringaren är inte verifierad. Be om anställningsnummer och använd request_verification_code och verify_caller först.");
        }
    }

    internal static string Summary(Fault f)
    {
        if (f.Affected == 0)
        {
            return $"Ett fel på {f.SiteName} påverkar inga tjänster som uppringaren har behörighet till.";
        }
        var parts = new List<string> { $"{f.Affected} tjänster påverkas, {f.Down} saknar fungerande väg" };
        if (f.CriticalDown > 0)
        {
            parts.Add($"{f.CriticalDown} av dem är kritiska");
        }
        if (f.FalseRedundancy > 0)
        {
            parts.Add($"{f.FalseRedundancy} har reservväg på papperet, men även den går via {f.SiteName} (falsk redundans)");
        }
        return string.Join(". ", parts) + $". Prioritet {f.Priority}.";
    }

    private static SpokenService Spoken(FaultService s) => new(s.Code, s.Name ?? "", s.ServiceType, s.Critical, s.Redundancy switch
    {
        Redundancy.Working => "reservväg fungerar",
        Redundancy.False => "falsk redundans: alla vägar går via felet",
        _ => "ingen reservväg",
    }, s.Paths);

    private static (string Type, long Id) Reference(string reference)
    {
        var value = reference.Trim();
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && value[..colon] is "site" or "equipment" or "cable"
            && long.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return (value[..colon], id);
        }
        throw new McpException("Ange stationen som \"site:ID\" från find_station, utrustning som \"equipment:ID\" eller kabel som \"cable:ID\".");
    }

    private static long SiteId(string station) => Reference(station) is ("site", var id) ? id
        : throw new McpException("Ange stationen som \"site:ID\" från find_station.");

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>The site type's name from the catalog, lower case as it is spoken (#208).</summary>
    private static string SiteType(string type) =>
        Cmdb.Catalog.TypeCatalog.Current.FindSiteType(type)?.Name.ToLowerInvariant() ?? type;

    private static string Status(string lifecycle) => lifecycle switch
    {
        "in_service" => "i drift",
        "planned" => "planerad",
        "under_construction" => "under byggnation",
        "decommissioning" => "under avveckling",
        "removed" => "borttagen",
        _ => lifecycle,
    };

    private static string Role(string role) => role switch
    {
        "technician" => "tekniker",
        "contractor" => "entreprenör",
        "noc" => "NOC",
        _ => role,
    };

    /// <summary>Northing in SWEREF 99 TM, roughly: the north above Sundsvall, the south below Jönköping.</summary>
    private static string Region(double? y) => y switch
    {
        null => "okänd",
        > 6_950_000 => "norr",
        > 6_450_000 => "mellersta",
        _ => "söder",
    };

    /// <summary>Runs a tool and logs the call (ADR-0015).</summary>
    private Task<T> AuditAsync<T>(string tool, Func<Task<T>> run, Func<T, string>? outcome = null, string? employeeId = null,
        Func<T, string?>? reference = null) =>
        VoiceAudit.RunAsync(system, User, tool, run, outcome, employeeId, reference);
}

internal static partial class VoiceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "On-call notified: {Incident} at {Site}, {Critical} critical service(s) down")]
    public static partial void OnCall(ILogger logger, string incident, string site, int critical);
}

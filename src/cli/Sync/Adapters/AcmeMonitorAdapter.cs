using System.Globalization;
using System.Text.Json.Nodes;

namespace Cmdb.Cli.Sync.Adapters;

/// <summary>
/// The reference adapter (#217) against ACME Monitor, a synthetic monitoring system. It shows the pattern for a REST
/// source: a bearer token from the environment, paged reads, and the source's own names mapped to the catalog by
/// configuration, never by code. The API is described in docs/adaptrar.md; the tests serve a fake one.
/// <code>
/// GET {url}/api/v1/sites?page=N    {"items":[{"id","code","name","type","lat","lon","state"}],"nextPage":N|null}
/// GET {url}/api/v1/devices?page=N  {"items":[{"id","site","rack","hostname","model","serial","firmware","state"}],"nextPage":N|null}
/// </code>
/// Settings: <c>url</c>; tables <c>models</c> (the source's model to a catalog key), <c>siteTypes</c> and <c>states</c>
/// (the source's state to a lifecycle). Secret: <c>CMDB_SYNC_ACME_MONITOR_TOKEN</c>.
/// </summary>
public sealed class AcmeMonitorAdapter : IAdapter
{
    private static readonly Dictionary<string, string> DefaultStates = new(StringComparer.Ordinal)
    {
        ["active"] = "in_service",
        ["planned"] = "planned",
        ["installing"] = "under_construction",
        ["retiring"] = "decommissioning",
        ["retired"] = "removed",
    };

    public string Name => "acme-monitor";

    public string Source => "acme-monitor";

    public string Description => "Syntetiskt övervakningssystem: siter och enheter (referensadapter)";

    public async Task ReadAsync(AdapterContext context, ExchangeWriter output, CancellationToken ct)
    {
        var url = new Uri(context.Setting("url").TrimEnd('/') + "/");
        var token = context.Secret("token");
        var models = context.Map("models");
        var siteTypes = context.Map("siteTypes");
        var states = context.Map("states");
        string Lifecycle(string? state, string what) =>
            state is null ? "in_service"
            : states.TryGetValue(state, out var mapped) || DefaultStates.TryGetValue(state, out mapped) ? mapped
            : Warned(context, $"{what}: okänt tillstånd '{state}', rapporteras som in_service", "in_service");

        // Sites by their code, which is what matching (source-matching.json) links on.
        var sites = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var site in AdapterContext.PagedAsync((page, c) => PageAsync(context, url, "api/v1/sites", page, token, c), ct))
        {
            var (id, code) = (Text(site, "id"), Text(site, "code"));
            if (id is null || code is null || Number(site, "lat") is not { } lat || Number(site, "lon") is not { } lon)
            {
                context.Warn($"site {id ?? "?"}: saknar id, kod eller position och hoppas över");
                continue;
            }
            var type = Text(site, "type") ?? "";
            output.Site(id, code, Text(site, "name") ?? code, siteTypes.GetValueOrDefault(type, type), Lifecycle(Text(site, "state"), $"site {id}"),
                lat: lat, lon: lon);
            sites.Add(id);
        }

        // Devices sit in a rack on a site; the rack is reported once per site as a location.
        var racks = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var device in AdapterContext.PagedAsync((page, c) => PageAsync(context, url, "api/v1/devices", page, token, c), ct))
        {
            var (id, site, rack, model) = (Text(device, "id"), Text(device, "site"), Text(device, "rack"), Text(device, "model"));
            if (id is null || site is null || rack is null || model is null)
            {
                context.Warn($"enhet {id ?? "?"}: saknar id, site, rack eller modell och hoppas över");
                continue;
            }
            if (!sites.Contains(site))
            {
                context.Warn($"enhet {id}: siten {site} rapporterades inte och enheten hoppas över");
                continue;
            }
            if (!models.TryGetValue(model, out var type))
            {
                context.Warn($"enhet {id}: modellen '{model}' saknas i konfigurationens models och enheten hoppas över");
                continue;
            }
            var location = $"{site}/{rack}";
            if (racks.Add(location))
            {
                output.Location(location, site, "rack", rack);
            }
            var attributes = new JsonObject();
            if (Text(device, "serial") is { } serial)
            {
                attributes["serialNumber"] = serial;
            }
            if (Text(device, "firmware") is { } firmware)
            {
                attributes["firmware"] = firmware;
            }
            output.Equipment(id, site, Text(device, "hostname") ?? id, type, Lifecycle(Text(device, "state"), $"enhet {id}"), location: location,
                attributes: attributes);
        }
    }

    private static async Task<Page<JsonNode>> PageAsync(AdapterContext context, Uri url, string path, string? page, string token, CancellationToken ct)
    {
        var body = await context.GetJsonAsync(new Uri(url, $"{path}?page={page ?? "1"}"), token, ct);
        var items = body["items"] as JsonArray ?? throw new AdapterException($"{path} svarade utan 'items'.");
        var next = body["nextPage"] is JsonValue n ? n.ToString() : null;
        return new Page<JsonNode>([.. items.OfType<JsonNode>()], next);
    }

    private static string Warned(AdapterContext context, string warning, string value)
    {
        context.Warn(warning);
        return value;
    }

    private static string? Text(JsonNode node, string name) =>
        node[name] is JsonValue v && v.ToString() is { Length: > 0 } text ? text : null;

    private static double? Number(JsonNode node, string name) =>
        node[name] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

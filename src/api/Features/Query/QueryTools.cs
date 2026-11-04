using Cmdb.Api.Auth;
using System.ComponentModel;
using Cmdb.Api.Agents;
using Cmdb.Catalog;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.Query;

public sealed record AgentSite(string Ref, string Code, string Name, string SiteType, string Lifecycle, int? Matching, string Url);

public sealed record FindSitesResult(long Total, IReadOnlyList<AgentSite> Sites, bool Truncated, double ElapsedMs);

/// <param name="Sites">The first few. The web app marks every match itself, with the same conditions and the person's own access.</param>
public sealed record ShowOnMapResult(long Total, IReadOnlyList<AgentSite> Sites, bool Truncated);

[McpServerToolType]
public sealed class QueryTools(RequestDb db, TypeCatalog catalog, AgentLinks links, IHttpContextAccessor http)
{
    public const int MaxLimit = 50;
    private const int ShownSites = 10;
    private const string PlaceHelp = "A Swedish municipality the sites lie in, for example \"Gävle\". The network has no other place names.";
    private const string WithinHelp = "With place: also sites this many kilometres outside the municipality, 0.1–200. \"Med omnejd\" or \"around\" without a distance: use 25 and say so.";
    private const string ExtentHelp = "Only sites in this area, [minX, minY, maxX, maxY] in EPSG:3006. For \"here\" or \"around here\" use the map view the app reports.";

    [McpServerTool(Name = "find_sites", Title = "Avancerad sökning", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Sites matching structured conditions, all combined with AND: site type, lifecycle, the site's own attributes, equipment the site holds " +
        "(category, model key, one attribute test, minimum count) and service types passing through. Returns the total and the " +
        "first sites by code. Call describe_catalog first to learn categories, model keys, attribute keys and allowed values.")]
    public async Task<FindSitesResult> FindSites(
        [Description("Site type keys (describe_catalog lists them with names and roles).")] string[]? siteTypes = null,
        [Description("Lifecycles, any of: planned, under_construction, in_service, decommissioning, removed.")] string[]? lifecycles = null,
        [Description("Up to five equipment conditions, each: category and/or typeKey (model key), optional attribute " +
            "{ key, op, value } with op one of eq, neq, gt, gte, lt, lte, prefix, contains, exists (no value), and optional minCount. " +
            "Example: [{ \"category\": \"radio\", \"attribute\": { \"key\": \"bandMHz\", \"op\": \"eq\", \"value\": 3500 } }].")]
        EquipmentCondition[]? equipment = null,
        [Description("Service type keys carried through the site (describe_catalog lists them).")] string[]? serviceTypes = null,
        [Description("Maximum sites listed, 1–50. The total is always returned.")] int limit = 25,
        [Description("Up to five tests on the site's own attributes, each { key, op, value } as for equipment; describe_catalog " +
            "lists the keys per site type. Example: [{ \"key\": \"backupHours\", \"op\": \"lt\", \"value\": 4 }].")]
        AttributeCondition[]? siteAttributes = null,
        [Description(PlaceHelp)] string? place = null,
        [Description(WithinHelp)] double? withinKm = null,
        [Description(ExtentHelp)] double[]? extent = null,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw new McpException($"limit must be between 1 and {MaxLimit}.");
        }
        var result = await RunAsync(new SiteQuery(siteTypes, lifecycles, equipment, serviceTypes, limit, siteAttributes, place, withinKm, extent), ct);
        return new FindSitesResult(result.Total, Agent(result), result.Total > result.Sites.Count, result.ElapsedMs);
    }

    [McpServerTool(Name = "show_on_map", Title = "Visa på kartan", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows sites to the person in the web app's map: marks every match and moves the view to them. Use it when someone in the web app's chat asks to " +
        "see or be shown sites (\"show me all sites in Gävle\"). Takes the same conditions as find_sites, and all matches are marked however many they are, so do " +
        "not list them one by one. Returns the total and the first few. Tell the person how many are marked; when Total is 0 nothing is marked, so say that. " +
        "Outside that chat nobody sees a map: use find_sites there.")]
    public async Task<ShowOnMapResult> ShowOnMap(
        [Description("Site type keys (describe_catalog lists them with names and roles).")] string[]? siteTypes = null,
        [Description("Lifecycles, any of: planned, under_construction, in_service, decommissioning, removed.")] string[]? lifecycles = null,
        [Description("Up to five equipment conditions, as for find_sites.")] EquipmentCondition[]? equipment = null,
        [Description("Service type keys carried through the site (describe_catalog lists them).")] string[]? serviceTypes = null,
        [Description("Up to five tests on the site's own attributes, as for find_sites.")] AttributeCondition[]? siteAttributes = null,
        [Description(PlaceHelp)] string? place = null,
        [Description(WithinHelp)] double? withinKm = null,
        [Description(ExtentHelp)] double[]? extent = null,
        CancellationToken ct = default)
    {
        var result = await RunAsync(new SiteQuery(siteTypes, lifecycles, equipment, serviceTypes, ShownSites, siteAttributes, place, withinKm, extent), ct);
        return new ShowOnMapResult(result.Total, Agent(result), result.Total > result.Sites.Count);
    }

    [McpServerTool(Name = "summarize_sites", Title = "Summera siter", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Counts sites matching the same conditions as find_sites, optionally per site type, lifecycle or region, and optionally sums a numeric " +
        "equipment attribute over them. Use it for \"how many\" and \"how much in total\", instead of listing. Example: sum powerDrawW for what sites draw.")]
    public async Task<SiteSummary> SummarizeSites(
        [Description("Site type keys.")] string[]? siteTypes = null,
        [Description("Lifecycles.")] string[]? lifecycles = null,
        [Description("Up to five equipment conditions, as for find_sites.")] EquipmentCondition[]? equipment = null,
        [Description("Service type keys carried through the site.")] string[]? serviceTypes = null,
        [Description("Tests on the site's own attributes, as for find_sites.")] AttributeCondition[]? siteAttributes = null,
        [Description(PlaceHelp)] string? place = null,
        [Description(WithinHelp)] double? withinKm = null,
        [Description(ExtentHelp)] double[]? extent = null,
        [Description("Group by: siteType, lifecycle or region (norr, mellersta, söder). Left out, one total.")] string? groupBy = null,
        [Description("A numeric equipment attribute to sum per group, e.g. powerDrawW.")] string? sumAttribute = null,
        CancellationToken ct = default)
    {
        var request = new SiteSummaryRequest(new SiteQuery(siteTypes, lifecycles, equipment, serviceTypes, 1, siteAttributes, place, withinKm, extent), groupBy, sumAttribute);
        var scope = http.HttpContext!.Scope();
        var errors = new SiteQueryValidator().Validate(request.Query).Errors.Select(e => e.ErrorMessage)
            .Concat(QuerySitesEndpoint.CatalogErrors(request.Query, catalog, scope))
            .Concat(SummarizeSitesEndpoint.Errors(request, catalog, scope))
            .ToList();
        if (errors.Count > 0)
        {
            throw new McpException(string.Join(" ", errors));
        }
        return await SummarizeSitesEndpoint.RunAsync(db, request, scope, ct);
    }

    private async Task<SiteQueryResult> RunAsync(SiteQuery query, CancellationToken ct)
    {
        var errors = new SiteQueryValidator().Validate(query).Errors.Select(e => e.ErrorMessage)
            .Concat(QuerySitesEndpoint.CatalogErrors(query, catalog, http.HttpContext!.Scope()))
            .ToList();
        if (errors.Count > 0)
        {
            throw new McpException(string.Join(" ", errors));
        }
        return await QuerySitesEndpoint.RunAsync(db, query, http.HttpContext!.Scope(), ct);
    }

    private List<AgentSite> Agent(SiteQueryResult result) =>
        [.. result.Sites.Select(s => new AgentSite(AgentLinks.Ref("site", s.Id), s.Code, s.Name, s.SiteType, s.Lifecycle, s.Matching, links.For("site", s.Id)))];

    [McpServerTool(Name = "describe_catalog", Title = "Typkatalog", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What exists and can be queried: site types (with names and roles such as hub or aggregation), lifecycles, service types, " +
        "equipment categories (with names, roles and their attributes: type and allowed values), every equipment model with its key, " +
        "and the attribute fields of each site, cable and service type.")]
    public Task<QueryFields> DescribeCatalog(CancellationToken ct = default) =>
        QueryFieldsEndpoint.LoadAsync(db, catalog, ct);
}

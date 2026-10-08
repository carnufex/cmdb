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

[McpServerToolType]
public sealed class QueryTools(RequestDb db, TypeCatalog catalog, AgentLinks links, IHttpContextAccessor http)
{
    public const int MaxLimit = 50;

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
        CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw new McpException($"limit must be between 1 and {MaxLimit}.");
        }
        var query = new SiteQuery(siteTypes, lifecycles, equipment, serviceTypes, limit, siteAttributes);
        var errors = new SiteQueryValidator().Validate(query).Errors.Select(e => e.ErrorMessage)
            .Concat(QuerySitesEndpoint.CatalogErrors(query, catalog, http.HttpContext!.Scope()))
            .ToList();
        if (errors.Count > 0)
        {
            throw new McpException(string.Join(" ", errors));
        }

        var result = await QuerySitesEndpoint.RunAsync(db, query, http.HttpContext!.Scope(), ct);
        return new FindSitesResult(
            result.Total,
            [.. result.Sites.Select(s => new AgentSite(AgentLinks.Ref("site", s.Id), s.Code, s.Name, s.SiteType, s.Lifecycle, s.Matching, links.For("site", s.Id)))],
            result.Total > result.Sites.Count,
            result.ElapsedMs);
    }

    [McpServerTool(Name = "describe_catalog", Title = "Typkatalog", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What exists and can be queried: site types (with names and roles such as hub or aggregation), lifecycles, service types, " +
        "equipment categories (with names, roles and their attributes: type and allowed values), every equipment model with its key, " +
        "and the attribute fields of each site, cable and service type.")]
    public Task<QueryFields> DescribeCatalog(CancellationToken ct = default) =>
        QueryFieldsEndpoint.LoadAsync(db, catalog, ct);
}

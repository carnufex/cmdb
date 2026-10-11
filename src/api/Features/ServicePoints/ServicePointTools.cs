using System.ComponentModel;
using Cmdb.Api.Agents;
using Cmdb.Api.Auth;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.ServicePoints;

public sealed record AgentServicePoint(string Ref, string Url, string Code, string? Address, string Type, int Units, string Status, double? X, double? Y,
    string? ServedBy);

public sealed record AgentServicePoints(int Total, int Units, IReadOnlyDictionary<string, int> ByStatus, IReadOnlyList<AgentServicePoint> ServicePoints,
    bool Truncated);

[McpServerToolType]
public sealed class ServicePointTools(RequestDb db, AgentLinks links, IHttpContextAccessor http)
{
    private const int Max = 200;

    [McpServerTool(Name = "find_service_points", Title = "Anslutningspunkter", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Service points (#292): addresses and customers the network is to reach (houses, blocks of flats with their flats as units, " +
        "businesses, masts), with status planned, ordered or connected and the site that serves a connected one. Find them in an area, " +
        "near a point or served by a site; counts per status and the units in all come with the list. Use get_object with " +
        "\"service-point:<id>\" for one.")]
    public async Task<AgentServicePoints> FindServicePoints(
        [Description("Optional area \"minx,miny,maxx,maxy\" in SWEREF 99 TM (EPSG:3006).")] string? bbox = null,
        [Description("Optional point \"x,y\" in SWEREF 99 TM, with radius.")] string? near = null,
        [Description("With near: metres, 1–50 000.")] double? radius = null,
        [Description("Optional: planned, ordered or connected.")] string? status = null,
        [Description("Optional: only points served by this site, \"site:<id>\".")] string? site = null,
        CancellationToken ct = default)
    {
        long? siteId = null;
        if (site is not null)
        {
            siteId = site.StartsWith("site:", StringComparison.Ordinal) && long.TryParse(site.AsSpan(5), out var s) ? s
                : throw new McpException("site is \"site:<id>\".");
        }
        var req = new ServicePointListRequest { Bbox = bbox, Near = near, Radius = radius, Status = status, Site = siteId, Limit = Max };
        var problems = new ServicePointListValidator().Validate(req).Errors;
        if (problems.Count > 0)
        {
            throw new McpException(string.Join(" ", problems.Select(e => e.ErrorMessage)));
        }
        var list = await ServicePointQueries.ListAsync(db, req, http.HttpContext!.Scope(), ct);
        return new AgentServicePoints(list.Total, list.Units, list.ByStatus,
            [.. list.Items.Select(p => new AgentServicePoint(AgentLinks.Ref(ServicePointPlans.Type, p.Id), links.For(ServicePointPlans.Type, p.Id), p.Code,
                p.Address, p.TypeKey, p.Units, p.Status, p.X, p.Y, p.SiteId is { } served ? AgentLinks.Ref("site", served) : null))],
            list.Total > list.Items.Count);
    }
}

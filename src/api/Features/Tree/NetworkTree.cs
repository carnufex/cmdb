using Cmdb.Api.Auth;
using Cmdb.Catalog;
using FastEndpoints;

namespace Cmdb.Api.Features.Tree;

/// <summary>The whole network as the content tree's root (#250): every site the caller can see, grouped by site type.</summary>
/// <param name="Label">"Sverige", or the caller's scopes by name when they see part of the network.</param>
public sealed record TreeRoot(string Label, int Sites, IReadOnlyList<TreeGroup> Groups);

/// <param name="Icon">The site type's icon (#249).</param>
public sealed record TreeGroup(string SiteType, string Name, string Icon, int Sites);

/// <summary>A range of sites by code, so that no level has more than <see cref="NetworkTree.ChunkSize"/> children.</summary>
public sealed record TreeChunk(string From, string To, int Sites);

public sealed record TreeSite(long Id, string Code, string Name, string Lifecycle);

/// <summary>A site type's sites, or the ranges they are split into when there are more than <see cref="NetworkTree.ChunkSize"/>.</summary>
public sealed record TreeGroupContent(IReadOnlyList<TreeChunk>? Chunks, IReadOnlyList<TreeSite>? Sites);

public sealed class TreeGroupRequest
{
    public string SiteType { get; set; } = "";

    /// <summary>The first and last code of a range from <see cref="TreeGroupContent.Chunks"/>; without them the group itself.</summary>
    [QueryParam]
    public string? From { get; set; }

    [QueryParam]
    public string? To { get; set; }
}

internal static class NetworkTree
{
    /// <summary>The most children a level of the tree has: a site type with more sites is split into ranges of codes.</summary>
    public const int ChunkSize = 500;
}

/// <summary>
/// The root of the content tree with nothing open (#250): the network, and the number of sites per site type, inside
/// the caller's scopes (#22). Each level is fetched when it is expanded.
/// </summary>
public sealed class GetNetworkTreeEndpoint(RequestDb db, TypeCatalog catalog, ScopeRegistry scopes) : EndpointWithoutRequest<TreeRoot>
{
    public override void Configure() => Get("/tree");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var cmd = db.CreateCommand($"""
            SELECT s.site_type, count(*)::int FROM site s WHERE s.lifecycle <> 'removed' AND {ScopeSql.Site("s.id", 1)} GROUP BY 1
            """))
        {
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }
        }
        // The catalog's order (the backbone first), then any site type it does not know.
        var groups = catalog.SiteTypes.Where(t => counts.ContainsKey(t.Key))
            .Select(t => new TreeGroup(t.Key, t.Name, CatalogIcons.For(t), counts[t.Key]))
            .Concat(counts.Keys.Where(k => catalog.FindSiteType(k) is null).Order(StringComparer.Ordinal).Select(k => new TreeGroup(k, k, "site", counts[k])))
            .ToList();
        var label = scope.Unrestricted
            ? "Sverige"
            : string.Join(", ", scope.Keys.Select(k => scopes.All.FirstOrDefault(s => s.Key == k)?.Name ?? k));
        await Send.OkAsync(new TreeRoot(label, counts.Values.Sum(), groups), ct);
    }
}

/// <summary>A site type's sites in the content tree (#250), or the ranges of codes they are split into, inside the caller's scopes.</summary>
public sealed class GetNetworkTreeGroupEndpoint(RequestDb db) : Endpoint<TreeGroupRequest, TreeGroupContent>
{
    public override void Configure() => Get("/tree/{siteType}");

    public override async Task HandleAsync(TreeGroupRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (req.From is not null && req.To is not null)
        {
            await Send.OkAsync(new TreeGroupContent(null, await SitesAsync(req.SiteType, req.From, req.To, scope, ct)), ct);
            return;
        }
        var chunks = new List<TreeChunk>();
        await using (var cmd = db.CreateCommand($"""
            SELECT n / {NetworkTree.ChunkSize}, min(code), max(code), count(*)::int
            FROM (SELECT s.code, (row_number() OVER (ORDER BY s.code) - 1)::int AS n
                  FROM site s WHERE s.site_type = $1 AND s.lifecycle <> 'removed' AND {ScopeSql.Site("s.id", 2)}) v
            GROUP BY 1 ORDER BY 1
            """))
        {
            cmd.Parameters.Add(new() { Value = req.SiteType });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                chunks.Add(new TreeChunk(reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            }
        }
        await Send.OkAsync(chunks.Count switch
        {
            0 => new TreeGroupContent(null, []),
            1 => new TreeGroupContent(null, await SitesAsync(req.SiteType, chunks[0].From, chunks[0].To, scope, ct)),
            _ => new TreeGroupContent(chunks, null),
        }, ct);
    }

    private async Task<List<TreeSite>> SitesAsync(string siteType, string from, string to, UserScope scope, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT s.id, s.code, s.name, s.lifecycle::text FROM site s
            WHERE s.site_type = $1 AND s.code BETWEEN $2 AND $3 AND s.lifecycle <> 'removed' AND {ScopeSql.Site("s.id", 4)}
            ORDER BY s.code LIMIT {NetworkTree.ChunkSize}
            """);
        cmd.Parameters.Add(new() { Value = siteType });
        cmd.Parameters.Add(new() { Value = from });
        cmd.Parameters.Add(new() { Value = to });
        cmd.Parameters.Add(scope.Parameter());
        var sites = new List<TreeSite>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sites.Add(new TreeSite(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return sites;
    }
}

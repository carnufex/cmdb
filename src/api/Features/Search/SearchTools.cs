using Cmdb.Api.Auth;
using System.ComponentModel;
using System.Globalization;
using Cmdb.Api.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace Cmdb.Api.Features.Search;

/// <summary>A search hit for an agent: stable reference and a link into the UI.</summary>
public sealed record AgentHit(string Ref, string Type, long Id, string Code, string? Name, string? Detail, string Lifecycle, string Url);

public sealed record SearchToolResult(IReadOnlyList<AgentHit> Hits, bool Truncated);

[McpServerToolType]
public sealed class SearchTools(RequestDb db, AgentLinks links, IHttpContextAccessor http)
{
    public const int MaxLimit = 50;

    [McpServerTool(Name = "search", Title = "Snabbsök", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Quick search over sites, equipment, cables, services and circuits by code, name, numeric id or equipment attribute text. " +
        "Ranked exact code first, then prefix, then contains. Use for names and codes; use find_sites for structured questions.")]
    public async Task<SearchToolResult> Search(
        [Description("Code, name or id, at least three characters unless numeric. Examples: \"RAD-000007\", \"SKP-01\", \"Radiosite 12\", \"1268\".")] string query,
        [Description("Maximum hits, 1–50.")] int limit = 10,
        CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length < 3 && !long.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new McpException("The query needs at least three characters, or a numeric id.");
        }
        if (limit is < 1 or > MaxLimit)
        {
            throw new McpException($"limit must be between 1 and {MaxLimit}.");
        }
        // Ask for one more than requested to know whether the answer was cut.
        var hits = await SearchEndpoint.RunAsync(db, q, null, null, limit + 1, http.HttpContext!.Scope(), ct);
        return new SearchToolResult(
            [.. hits.Take(limit).Select(h => new AgentHit(AgentLinks.Ref(h.Type, h.Id), h.Type, h.Id, h.Code, h.Name, h.Detail, h.Lifecycle, links.For(h.Type, h.Id)))],
            hits.Count > limit);
    }
}

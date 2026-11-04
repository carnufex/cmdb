using System.ComponentModel;
using Cmdb.Api.Auth;
using ModelContextProtocol.Server;

namespace Cmdb.Api.Features.CurrentUser;

/// <param name="Scopes">The access scopes that apply, by name.</param>
/// <param name="SeesWholeNetwork">False means some objects exist that this person cannot see, so an empty answer may only be outside their access.</param>
/// <param name="HidesPositions">Positions are hidden, so searching by place or area is refused.</param>
/// <param name="HiddenAttributes">Attributes the person cannot see or search on.</param>
/// <param name="MayRequestChanges">May ask for changes with request_change.</param>
public sealed record AgentAccess(string Username, IReadOnlyList<string> Scopes, bool SeesWholeNetwork, bool HidesPositions, IReadOnlyList<string> HiddenAttributes, bool MayRequestChanges);

[McpServerToolType]
public sealed class AccessTools(ScopeRegistry registry, IHttpContextAccessor http)
{
    [McpServerTool(Name = "my_access", Title = "Min behörighet", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What the person asking may see and do. Use it to explain an empty or refused answer: nothing found may mean outside their access, which is different from nothing existing.")]
    public AgentAccess MyAccess()
    {
        var user = http.HttpContext!.User;
        var scope = http.HttpContext!.Scope();
        return new AgentAccess(user.Identity?.Name ?? "?",
            [.. scope.Keys.Select(k => registry.All.FirstOrDefault(s => s.Key == k)?.Name ?? k)],
            scope.Unrestricted, scope.HidesCoordinates,
            [.. scope.HiddenAttributes.Where(a => a != UserScope.HiddenCoordinates).Order(StringComparer.Ordinal)],
            user.IsInRole("cmdb-full"));
    }
}

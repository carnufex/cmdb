using Cmdb.Api.Auth;
using FastEndpoints;

namespace Cmdb.Api.Features.CurrentUser;

/// <param name="Client">The client the token was issued to: cmdb-web, cmdb-mcp or an agent client.</param>
/// <param name="Scopes">The access scopes that apply (#22), by name; empty means nothing is visible.</param>
public sealed record MeResponse(string Username, string? Name, string? Email, IReadOnlyList<string> Groups, string? Client = null, IReadOnlyList<string>? Scopes = null);

/// <summary>The signed-in user as the API sees them, including the groups that will map to access scopes.</summary>
public sealed class MeEndpoint : EndpointWithoutRequest<MeResponse>
{
    public override void Configure() => Get("/me");

    public override Task HandleAsync(CancellationToken ct)
    {
        var groups = User.FindAll(CmdbClaims.Groups).Select(c => c.Value).Order(StringComparer.Ordinal).ToList();
        return Send.OkAsync(
            new MeResponse(
                User.FindFirst(CmdbClaims.Username)?.Value ?? "",
                User.FindFirst(CmdbClaims.Name)?.Value,
                User.FindFirst(CmdbClaims.Email)?.Value,
                groups,
                User.FindFirst(CmdbClaims.Client)?.Value,
                [.. HttpContext.Scope().Keys.Select(k => Resolve<Cmdb.Api.Auth.ScopeRegistry>().All.FirstOrDefault(s => s.Key == k)?.Name ?? k)]),
            ct);
    }
}

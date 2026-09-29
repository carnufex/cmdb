using Cmdb.Api.Auth;
using FastEndpoints;

namespace Cmdb.Api.Features.CurrentUser;

/// <param name="Client">The client the token was issued to: cmdb-web, cmdb-mcp or an agent client.</param>
public sealed record MeResponse(string Username, string? Name, string? Email, IReadOnlyList<string> Groups, string? Client = null);

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
                User.FindFirst(CmdbClaims.Client)?.Value),
            ct);
    }
}

using System.Security.Claims;
using System.Threading.RateLimiting;
using Cmdb.Api.Agents;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Cmdb.Api.Auth;

/// <summary>
/// OIDC bearer tokens from Authentik (ADR-0008). Every endpoint requires a token unless it opts out.
/// Authentik issues tokens per application, so the API trusts the cmdb applications (web, MCP clients, agent service
/// accounts, ADR-0011) by issuer and audience, and nothing else the same Authentik serves.
/// </summary>
public static class AuthSetup
{
    public static IServiceCollection AddCmdbAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var auth = config.GetSection("Auth");
        var trust = TrustedApplications.From(auth);

        services
            .AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                // A 401 carries WWW-Authenticate with resource_metadata, so MCP clients discover the IdP themselves.
                o.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(o =>
            {
                // The browser reaches the IdP on its public URL, which becomes the token issuer, while the
                // API may have to fetch metadata over an internal address (e.g. the compose service name).
                // All cmdb applications sign with the same key, so one metadata document serves them all.
                o.Authority = trust.Authority;
                o.MetadataAddress = auth["MetadataAddress"] ?? $"{trust.Authority.TrimEnd('/')}/.well-known/openid-configuration";
                o.RequireHttpsMetadata = auth.GetValue("RequireHttpsMetadata", true);
                o.MapInboundClaims = false;
                o.TokenValidationParameters.ValidIssuers = trust.Issuers;
                o.TokenValidationParameters.ValidAudiences = trust.Audiences;
                o.TokenValidationParameters.NameClaimType = CmdbClaims.Username;
                o.TokenValidationParameters.RoleClaimType = CmdbClaims.Groups;
            })
            .AddMcp(o =>
            {
                o.ResourceMetadata = new() { AuthorizationServers = { trust.McpAuthorizationServer } };
                // The resource is where the caller reached us: the demo behind Cloudflare, or localhost.
                o.Events.OnResourceMetadataRequest = context =>
                {
                    var request = context.HttpContext.Request;
                    var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
                    context.ResourceMetadata = new()
                    {
                        Resource = $"{scheme}://{request.Host}{McpSetup.Path}",
                        AuthorizationServers = { trust.McpAuthorizationServer },
                        ScopesSupported = ["openid", "profile", "email", "offline_access"],
                        BearerMethodsSupported = ["header"],
                        ResourceName = "CMDB (syntetisk data)",
                    };
                    return Task.CompletedTask;
                };
            })
            // The operations agent's voice channel (ADR-0015): a shared secret, and the caller's scopes once verified.
            .AddScheme<AuthenticationSchemeOptions, Cmdb.Api.Features.Voice.VoiceAuthenticationHandler>(
                Cmdb.Api.Features.Voice.VoiceAuthenticationHandler.SchemeName, null);
        services.AddAuthorization(o => o.AddPolicy(Cmdb.Api.Features.Voice.VoiceAuthenticationHandler.SchemeName, p => p
            .AddAuthenticationSchemes(Cmdb.Api.Features.Voice.VoiceAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()));

        // Agents (anything on /mcp, and the agent service-account clients on /api) are limited per client and user,
        // so one agent cannot bulk-export the network. People in the web UI are not limited.
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            var burst = auth.GetValue("RateLimit:Burst", 60);
            var perSecond = auth.GetValue("RateLimit:PerSecond", 20);
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var client = context.User.FindFirstValue(CmdbClaims.Client);
                var isAgent = context.Request.Path.StartsWithSegments(McpSetup.Path, StringComparison.Ordinal)
                    || context.Request.Path.StartsWithSegments(McpSetup.VoicePath, StringComparison.Ordinal)
                    || (client is not null && trust.AgentClients.Contains(client));
                return isAgent
                    ? RateLimitPartition.GetTokenBucketLimiter($"{client}|{context.User.FindFirstValue(CmdbClaims.Subject)}", _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = burst,
                        TokensPerPeriod = perSecond,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetNoLimiter("people");
            });
        });
        services.AddSingleton(trust);
        return services;
    }
}

/// <summary>The cmdb applications in Authentik whose tokens the API accepts.</summary>
public sealed record TrustedApplications(string Authority, string[] Issuers, string[] Audiences, string McpAuthorizationServer, string[] AgentClients)
{
    /// <summary>
    /// <c>Auth:Authority</c> is the web application's issuer (…/application/o/cmdb/) with audience <c>Auth:Audience</c>.
    /// <c>Auth:TrustedApplications</c> names further applications; their slug is also their client id.
    /// </summary>
    public static TrustedApplications From(IConfigurationSection auth)
    {
        var authority = auth["Authority"] ?? throw new InvalidOperationException("Auth:Authority is not configured.");
        var audience = auth["Audience"] ?? throw new InvalidOperationException("Auth:Audience is not configured.");
        var others = auth.GetSection("TrustedApplications").Get<string[]>() ?? [];
        var baseUrl = authority.TrimEnd('/');
        baseUrl = baseUrl[..(baseUrl.LastIndexOf('/') + 1)];
        string IssuerOf(string slug) => $"{baseUrl}{slug}/";
        var mcp = auth["McpClient"] ?? "cmdb-mcp";
        return new TrustedApplications(
            authority,
            [authority, .. others.Select(IssuerOf)],
            [audience, .. others],
            IssuerOf(mcp),
            auth.GetSection("AgentClients").Get<string[]>() ?? ["cmdb-agents"]);
    }
}

public static class CmdbClaims
{
    public const string Subject = "sub";
    public const string Username = "preferred_username";
    public const string Name = "name";
    public const string Email = "email";
    public const string Groups = "groups";

    /// <summary>The client the token was issued to: the web app, an MCP client or an agent service account.</summary>
    public const string Client = "azp";
}

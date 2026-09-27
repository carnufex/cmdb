using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Cmdb.Api.Auth;

/// <summary>OIDC bearer tokens from Authentik (ADR-0008). Every endpoint requires a token unless it opts out.</summary>
public static class AuthSetup
{
    public static IServiceCollection AddCmdbAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var auth = config.GetSection("Auth");
        var authority = auth["Authority"] ?? throw new InvalidOperationException("Auth:Authority is not configured.");
        var audience = auth["Audience"] ?? throw new InvalidOperationException("Auth:Audience is not configured.");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                // The browser reaches the IdP on its public URL, which becomes the token issuer, while the
                // API may have to fetch metadata over an internal address (e.g. the compose service name).
                o.Authority = authority;
                o.MetadataAddress = auth["MetadataAddress"] ?? $"{authority.TrimEnd('/')}/.well-known/openid-configuration";
                o.RequireHttpsMetadata = auth.GetValue("RequireHttpsMetadata", true);
                o.Audience = audience;
                o.MapInboundClaims = false;
                o.TokenValidationParameters.ValidIssuer = authority;
                o.TokenValidationParameters.NameClaimType = CmdbClaims.Username;
                o.TokenValidationParameters.RoleClaimType = CmdbClaims.Groups;
            });
        services.AddAuthorization();
        return services;
    }
}

public static class CmdbClaims
{
    public const string Username = "preferred_username";
    public const string Name = "name";
    public const string Email = "email";
    public const string Groups = "groups";
}

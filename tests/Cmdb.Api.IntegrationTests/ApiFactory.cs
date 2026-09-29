using Cmdb.Catalog;
using Cmdb.Database;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Cmdb.Api.IntegrationTests.ApiFactory))]

namespace Cmdb.Api.IntegrationTests;

/// <summary>
/// The API wired to a real, migrated PostGIS in a throwaway container. Shared by all tests in the assembly.
/// Tests that need an untouched database create one with <see cref="NewDatabaseAsync"/>.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PostGisImage = "postgis/postgis:17-3.5";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostGisImage).Build();
    private int _databases;

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource Db { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Db = CmdbDatabase.CreateDataSource(ConnectionString);
        await CmdbDatabase.MigrateAsync(Db);
        await using var context = CmdbDatabase.CreateContext(Db);
        await CatalogSync.SyncAsync(context, TypeCatalog.Embedded);
        // Starting the host loads the graph in the background; tests begin once it is in place.
        await Services.GetRequiredService<Cmdb.Graph.GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Creates an empty database with PostGIS available but no migrations applied.</summary>
    public async Task<NpgsqlDataSource> NewDatabaseAsync()
    {
        var name = $"t{Interlocked.Increment(ref _databases)}";
        await using (var cmd = Db.CreateCommand($"CREATE DATABASE {name} TEMPLATE template_postgis"))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        return CmdbDatabase.CreateDataSource(new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString);
    }

    public const string Issuer = "https://idp.test/application/o/cmdb/";
    public const string Audience = "cmdb-web";

    // Tokens are signed locally; the API is pointed at this key instead of fetching IdP metadata.
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test" };

    /// <summary>Issues a token shaped like Authentik's, with the claims the API reads.</summary>
    public static string Token(
        string username = "cmdb-demo-full",
        IEnumerable<string>? groups = null,
        string audience = Audience,
        DateTime? expires = null,
        SecurityKey? key = null,
        string issuer = Issuer)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = $"sub-{username}",
            ["preferred_username"] = username,
            ["name"] = $"Demo {username}",
            ["email"] = $"{username}@cmdb.local",
            ["groups"] = (groups ?? ["cmdb-full"]).ToArray(),
            // Authentik sets azp to the client the token was issued to.
            ["azp"] = audience,
        };
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = now.AddMinutes(-10),
            NotBefore = now.AddMinutes(-10),
            Expires = expires ?? now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A client that sends a valid bearer token.</summary>
    public HttpClient CreateAuthenticatedClient(string username = "cmdb-demo-full", IEnumerable<string>? groups = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(username, groups));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Cmdb", ConnectionString);
        builder.UseSetting("Auth:Authority", Issuer);
        builder.UseSetting("Auth:Audience", Audience);
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = null;
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
            }));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Db.DisposeAsync();
        await _container.DisposeAsync();
    }
}

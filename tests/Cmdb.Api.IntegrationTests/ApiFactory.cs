using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Cmdb.Api.IntegrationTests.ApiFactory))]

namespace Cmdb.Api.IntegrationTests;

/// <summary>The API wired to a real PostGIS in a throwaway container. Shared by all tests in the assembly.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PostGisImage = "postgis/postgis:17-3.5";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder(PostGisImage).Build();

    public string ConnectionString => _db.GetConnectionString();

    public async ValueTask InitializeAsync() => await _db.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Cmdb", ConnectionString);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
    }
}

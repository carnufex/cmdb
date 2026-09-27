using Cmdb.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
        Db = NpgsqlDataSource.Create(ConnectionString);
        await Migrator.MigrateAsync(Db);
    }

    /// <summary>Creates an empty database with PostGIS available but no migrations applied.</summary>
    public async Task<NpgsqlDataSource> NewDatabaseAsync()
    {
        var name = $"t{Interlocked.Increment(ref _databases)}";
        await using (var cmd = Db.CreateCommand($"CREATE DATABASE {name} TEMPLATE template_postgis"))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        return NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Cmdb", ConnectionString);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Db.DisposeAsync();
        await _container.DisposeAsync();
    }
}

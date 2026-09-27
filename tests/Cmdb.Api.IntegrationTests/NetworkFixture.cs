using Cmdb.Catalog;
using Cmdb.DataGen;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Cmdb.Api.IntegrationTests;

/// <summary>
/// The small synthetic network (seed 1) in its own database, with an API instance pointed at it. Built once per
/// test run and shared, since loading takes a second or two.
/// </summary>
public static class NetworkFixture
{
    private static Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)>? _instance;
    private static readonly Lock Gate = new();

    public static Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> GetAsync(ApiFactory factory)
    {
        lock (Gate)
        {
            return _instance ??= CreateAsync(factory);
        }
    }

    public static HttpClient Client(WebApplicationFactory<Program> api, string username = "cmdb-demo-full", IEnumerable<string>? groups = null)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(username, groups));
        return client;
    }

    private static async Task<(NpgsqlDataSource, WebApplicationFactory<Program>)> CreateAsync(ApiFactory factory)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Embedded), reset: false, TextWriter.Null);
        // The data source hides the password, so rebuild from the container's connection string.
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        return (db, api);
    }
}

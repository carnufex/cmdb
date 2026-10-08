using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cmdb.Api.IntegrationTests;

/// <summary>
/// The small synthetic network (seed 1) in its own database, with an API instance pointed at it. Built once per
/// test run and shared, since loading takes a second or two.
/// </summary>
public static class NetworkFixture
{
    private static Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)>? _instance;
    private static Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)>? _scenarios;
    private static readonly Lock Gate = new();

    public static Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> GetAsync(ApiFactory factory)
    {
        lock (Gate)
        {
            return _instance ??= CreateAsync(factory);
        }
    }

    /// <summary>The same network with the operations agent's demo scenarios (#132), in a database of its own.</summary>
    public static Task<(NpgsqlDataSource Db, WebApplicationFactory<Program> Api)> WithScenariosAsync(ApiFactory factory)
    {
        lock (Gate)
        {
            return _scenarios ??= CreateAsync(factory, scenarios: true);
        }
    }

    public static HttpClient Client(WebApplicationFactory<Program> api, string username = "cmdb-demo-full", IEnumerable<string>? groups = null)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(username, groups));
        return client;
    }

    private static async Task<(NpgsqlDataSource, WebApplicationFactory<Program>)> CreateAsync(ApiFactory factory, bool scenarios = false)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, scenarios: scenarios);
        // The data source hides the password, so rebuild from the container's connection string.
        var connectionString = new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
        }.ConnectionString;
        var api = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cmdb", connectionString));
        await api.Services.GetRequiredService<GraphHolder>().Ready.WaitAsync(TimeSpan.FromSeconds(60));
        return (db, api);
    }
}

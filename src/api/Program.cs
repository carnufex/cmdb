using Cmdb.Api.Agents;
using Cmdb.Api.Auth;
using Cmdb.Api.Diagnostics;
using Cmdb.Catalog;
using Cmdb.Database;
using FastEndpoints;
using Npgsql;

// Container healthcheck: the runtime image has no curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/health/ready"));
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Cmdb")
    ?? throw new InvalidOperationException("ConnectionStrings:Cmdb is not configured.");
builder.Services.AddSingleton(_ => CmdbDatabase.CreateDataSource(connectionString));
builder.Services.AddDbContext<CmdbDbContext>((sp, o) => o.UseCmdb(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton(TypeCatalog.Embedded);
builder.Services.AddCmdbAuthentication(builder.Configuration);
builder.Services.AddFastEndpoints();
builder.Services.AddCmdbMcp();

var app = builder.Build();

// In production migrations and the catalog sync run as a separate step (job or init container): `dotnet Cmdb.Api.dll --migrate`.
var migrateOnly = args.Contains("--migrate");
if (migrateOnly || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    var applied = await CmdbDatabase.MigrateAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
    StartupLog.MigrationsApplied(app.Logger, applied.Count, applied);
    // The type catalog is versioned data and ships with the schema.
    await using var scope = app.Services.CreateAsyncScope();
    var synced = await CatalogSync.SyncAsync(scope.ServiceProvider.GetRequiredService<CmdbDbContext>(), TypeCatalog.Embedded);
    StartupLog.CatalogSynced(app.Logger, synced);
    if (migrateOnly)
    {
        return 0;
    }
}

app.UseServerTiming();
app.UseAuthentication();
app.UseAuthorization();
app.UseFastEndpoints(c => c.Endpoints.RoutePrefix = "api");
app.MapCmdbMcp();
await app.RunAsync();
return 0;

public partial class Program;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Count} migration(s): {Versions}")]
    public static partial void MigrationsApplied(ILogger logger, int count, IReadOnlyList<string> versions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Equipment type catalog synced, {Count} type(s) written")]
    public static partial void CatalogSynced(ILogger logger, int count);
}

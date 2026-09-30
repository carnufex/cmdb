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
// One pool as the table owner. Request paths take RequestDb and apply the caller's scopes; system work takes SystemDb.
// Row-level security only applies to roles reading the database directly (ADR-0012). NpgsqlDataSource itself is not
// registered, so code that has not chosen fails at startup.
var dataSource = CmdbDatabase.CreateDataSource(connectionString);
// Created by a factory so the container disposes it, and the pool with it, on shutdown.
builder.Services.AddSingleton(_ => new Cmdb.Api.Auth.SystemDb(dataSource));
builder.Services.AddSingleton(new Cmdb.Api.Auth.RequestDb(dataSource));
builder.Services.AddDbContext<CmdbDbContext>((sp, o) => o.UseCmdb(sp.GetRequiredService<Cmdb.Api.Auth.RequestDb>().Source));
builder.Services.AddSingleton(TypeCatalog.Embedded);
builder.Services.AddCmdbAuthentication(builder.Configuration);
builder.Services.AddFastEndpoints();
builder.Services.AddCmdbMcp();
builder.Services.AddSingleton<Cmdb.Graph.GraphHolder>();
// Access scopes (#22): the caller's scopes per request, the materialised visibility, and graph masks.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Cmdb.Api.Auth.ScopeRegistry>();
builder.Services.AddSingleton<Cmdb.Api.Auth.ScopeMasks>();
builder.Services.AddSingleton<Cmdb.Api.Features.Plans.PlanViews>();
builder.Services.AddScoped<Cmdb.Api.Features.Plans.PlanWrites>();
builder.Services.AddSingleton<Cmdb.Api.Auth.ScopeRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Cmdb.Api.Auth.ScopeRefreshService>());
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User is { Identity.IsAuthenticated: true } user
    ? sp.GetRequiredService<Cmdb.Api.Auth.ScopeRegistry>().For(user)
    : Cmdb.Api.Auth.UserScope.None);
builder.Services.AddSingleton<Cmdb.Graph.IGraphChangeFeed>(sp => new Cmdb.Graph.PostgresGraphChangeFeed(sp.GetRequiredService<Cmdb.Api.Auth.SystemDb>().Source));
builder.Services.AddHostedService<Cmdb.Api.Features.Graph.GraphLoadingService>();
builder.Services.AddHostedService<Cmdb.Api.Features.Graph.GraphChangePruning>();
builder.Services.AddCmdbOpenApi();

var app = builder.Build();

// In production migrations and the catalog sync run as a separate step (job or init container): `dotnet Cmdb.Api.dll --migrate`.
var migrateOnly = args.Contains("--migrate");
if (migrateOnly || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    var applied = await CmdbDatabase.MigrateAsync(app.Services.GetRequiredService<Cmdb.Api.Auth.SystemDb>().Source);
    StartupLog.MigrationsApplied(app.Logger, applied.Count, applied);
    // The type catalog is versioned data and ships with the schema.
    await using var scope = app.Services.CreateAsyncScope();
    var synced = await CatalogSync.SyncAsync(scope.ServiceProvider.GetRequiredService<CmdbDbContext>(), TypeCatalog.Embedded);
    StartupLog.CatalogSynced(app.Logger, synced);
    // Access scopes (#22) are synthetic demo data too; what they show is materialised after the sync.
    await Cmdb.Database.Scopes.ScopeCatalog.SyncAsync(scope.ServiceProvider.GetRequiredService<CmdbDbContext>());
    await Cmdb.Database.Scopes.ScopeVisibility.RefreshAsync(app.Services.GetRequiredService<Cmdb.Api.Auth.SystemDb>().Source);
    await Cmdb.Database.Scopes.DirectAccess.GrantAsync(app.Services.GetRequiredService<Cmdb.Api.Auth.SystemDb>().Source);
    if (migrateOnly)
    {
        return 0;
    }
}

app.UseCmdbOpenApi();
app.UseServerTiming();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAgentLogging();
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

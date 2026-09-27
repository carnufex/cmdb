using Cmdb.Database.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Cmdb.Database;

/// <summary>
/// The schema's single source of truth (ADR-0009). Change the model, then add a migration:
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/database</c>.
/// </summary>
public sealed class CmdbDbContext(DbContextOptions<CmdbDbContext> options) : DbContext(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();
    public DbSet<Equipment> Equipment => Set<Equipment>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<Port> Ports => Set<Port>();
    public DbSet<CableType> CableTypes => Set<CableType>();
    public DbSet<Cable> Cables => Set<Cable>();
    public DbSet<Conductor> Conductors => Set<Conductor>();
    public DbSet<ConductorEnd> ConductorEnds => Set<ConductorEnd>();
    public DbSet<Connection> Connections => Set<Connection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");

        // Declared explicitly so the labels keep their natural order; enum order is what ORDER BY and < use.
        modelBuilder.HasPostgresEnum("lifecycle_state", ["planned", "under_construction", "in_service", "decommissioning", "removed"]);
        modelBuilder.HasPostgresEnum("terminal_kind", ["port", "conductor_end"]);
        modelBuilder.HasPostgresEnum("connection_kind", ["patch", "splice", "termination", "internal"]);
        modelBuilder.HasPostgresEnum("cable_medium", ["fiber", "copper", "coax", "power"]);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CmdbDbContext).Assembly);
    }
}

/// <summary>Wiring shared by the API, the data generator and the tests.</summary>
public static class CmdbDatabase
{
    /// <summary>
    /// A data source with the Postgres enums and PostGIS mapped, for both EF and direct Npgsql use
    /// (bulk COPY, graph loading).
    /// </summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNetTopologySuite();
        builder.MapEnum<LifecycleState>("lifecycle_state");
        builder.MapEnum<TerminalKind>("terminal_kind");
        builder.MapEnum<ConnectionKind>("connection_kind");
        builder.MapEnum<CableMedium>("cable_medium");
        return builder.Build();
    }

    public static DbContextOptionsBuilder UseCmdb(this DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            .UseNpgsql(dataSource, o => o
                .UseNetTopologySuite()
                .MapEnum<LifecycleState>("lifecycle_state")
                .MapEnum<TerminalKind>("terminal_kind")
                .MapEnum<ConnectionKind>("connection_kind")
                .MapEnum<CableMedium>("cable_medium"))
            .UseSnakeCaseNamingConvention();

    public static CmdbDbContext CreateContext(NpgsqlDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<CmdbDbContext>();
        options.UseCmdb(dataSource);
        return new CmdbDbContext(options.Options);
    }

    /// <summary>Applies pending migrations. EF Core locks the history table, so concurrent pods are safe.</summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var db = CreateContext(dataSource);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        await db.Database.MigrateAsync(ct);
        // Enums and PostGIS types created by a migration must be visible to already open connections.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ReloadTypesAsync(ct);
        return pending;
    }
}

/// <summary>Used by <c>dotnet ef</c> at design time. Adding a migration needs no database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CmdbDbContext>
{
    public CmdbDbContext CreateDbContext(string[] args) =>
        CmdbDatabase.CreateContext(CmdbDatabase.CreateDataSource(
            Environment.GetEnvironmentVariable("ConnectionStrings__Cmdb") ?? "Host=localhost;Database=cmdb;Username=cmdb"));
}

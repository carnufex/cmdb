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
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<Circuit> Circuits => Set<Circuit>();
    public DbSet<CircuitHop> CircuitHops => Set<CircuitHop>();
    public DbSet<CircuitDependency> CircuitDependencies => Set<CircuitDependency>();
    public DbSet<ServiceCircuit> ServiceCircuits => Set<ServiceCircuit>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<GraphChange> GraphChanges => Set<GraphChange>();
    public DbSet<GraphChangePruned> GraphChangePruned => Set<GraphChangePruned>();
    public DbSet<AccessScope> AccessScopes => Set<AccessScope>();
    public DbSet<ScopeSite> ScopeSites => Set<ScopeSite>();
    public DbSet<ScopeCable> ScopeCables => Set<ScopeCable>();
    public DbSet<ScopeRouteSegment> ScopeRouteSegments => Set<ScopeRouteSegment>();
    public DbSet<ScopeCircuit> ScopeCircuits => Set<ScopeCircuit>();
    public DbSet<ScopeService> ScopeServices => Set<ScopeService>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanDependency> PlanDependencies => Set<PlanDependency>();
    public DbSet<PlanOperation> PlanOperations => Set<PlanOperation>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ChangelogEntry> ChangelogEntries => Set<ChangelogEntry>();
    public DbSet<VoiceCaller> VoiceCallers => Set<VoiceCaller>();
    public DbSet<VoiceSms> VoiceSms => Set<VoiceSms>();
    public DbSet<VoiceChallenge> VoiceChallenges => Set<VoiceChallenge>();
    public DbSet<VoiceSession> VoiceSessions => Set<VoiceSession>();
    public DbSet<VoiceToolCall> VoiceToolCalls => Set<VoiceToolCall>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<PlannedWork> PlannedWorks => Set<PlannedWork>();
    public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();
    public DbSet<Classification> Classifications => Set<Classification>();
    public DbSet<SourceRecord> SourceRecords => Set<SourceRecord>();
    public DbSet<DuctType> DuctTypes => Set<DuctType>();
    public DbSet<RouteSegment> RouteSegments => Set<RouteSegment>();
    public DbSet<Duct> Ducts => Set<Duct>();
    public DbSet<DuctSegment> DuctSegments => Set<DuctSegment>();
    public DbSet<Subduct> Subducts => Set<Subduct>();
    public DbSet<CablePath> CablePaths => Set<CablePath>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("pg_trgm");

        // Declared explicitly so the labels keep their natural order; enum order is what ORDER BY and < use.
        modelBuilder.HasPostgresEnum("lifecycle_state", ["planned", "under_construction", "in_service", "decommissioning", "removed"]);
        modelBuilder.HasPostgresEnum("terminal_kind", ["port", "conductor_end"]);
        modelBuilder.HasPostgresEnum("connection_kind", ["patch", "splice", "termination", "internal"]);
        modelBuilder.HasPostgresEnum("cable_medium", ["fiber", "copper", "coax", "power"]);
        modelBuilder.HasPostgresEnum("circuit_layer", ["physical", "transmission", "logical"]);
        modelBuilder.HasPostgresEnum("channel_kind", ["wavelength", "timeslot", "vlan"]);
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
        builder.MapEnum<CircuitLayer>("circuit_layer");
        builder.MapEnum<ChannelKind>("channel_kind");
        return builder.Build();
    }

    public static DbContextOptionsBuilder UseCmdb(this DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            .UseNpgsql(dataSource, o => o
                .UseNetTopologySuite()
                .MapEnum<LifecycleState>("lifecycle_state")
                .MapEnum<TerminalKind>("terminal_kind")
                .MapEnum<ConnectionKind>("connection_kind")
                .MapEnum<CableMedium>("cable_medium")
                .MapEnum<CircuitLayer>("circuit_layer")
                .MapEnum<ChannelKind>("channel_kind"))
            .UseSnakeCaseNamingConvention();

    public static CmdbDbContext CreateContext(NpgsqlDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<CmdbDbContext>();
        options.UseCmdb(dataSource);
        return new CmdbDbContext(options.Options);
    }

    // Arbitrary but fixed key for pg_advisory_lock.
    private const long MigrationLockKey = 0x434D_4442_4D49_4752;

    /// <summary>
    /// Applies pending migrations. A session advisory lock serialises concurrent runs (several pods, a job and
    /// the data generator); EF Core does not prevent two processes from applying the same migration.
    /// </summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var lockConnection = await dataSource.OpenConnectionAsync(ct);
        await using (var acquire = new NpgsqlCommand($"SELECT pg_advisory_lock({MigrationLockKey})", lockConnection))
        {
            await acquire.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using var db = CreateContext(dataSource);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            await db.Database.MigrateAsync(ct);
            // Enums and PostGIS types created by a migration must be visible to already open connections.
            await lockConnection.ReloadTypesAsync(ct);
            return pending;
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({MigrationLockKey})", lockConnection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

/// <summary>Used by <c>dotnet ef</c> at design time. Adding a migration needs no database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CmdbDbContext>
{
    public CmdbDbContext CreateDbContext(string[] args) =>
        CmdbDatabase.CreateContext(CmdbDatabase.CreateDataSource(
            Environment.GetEnvironmentVariable("ConnectionStrings__Cmdb") ?? "Host=localhost;Database=cmdb;Username=cmdb"));
}

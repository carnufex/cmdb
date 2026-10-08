using Cmdb.Catalog;
using Cmdb.DataGen;
using Cmdb.DataGen.Geo;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.DataGen;

public sealed class LoaderTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Loads_the_small_network_with_foreign_key_checks_on()
    {
        await using var db = await factory.NewDatabaseAsync();
        var network = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current);

        // fast: false keeps every foreign key checked per row, which proves the generator's references.
        await Loader.LoadAsync(db, network, reset: false, TextWriter.Null, fast: false, ct: Ct);

        (await Count(db, "site")).ShouldBe(network.Sites.Count);
        (await Count(db, "equipment")).ShouldBe(network.Equipment.Count);
        (await Count(db, "port")).ShouldBe(network.Ports);
        (await Count(db, "terminal")).ShouldBe(network.Terminals);
        (await Count(db, "connection")).ShouldBe(network.Connections.Count);
        (await Count(db, "circuit_hop")).ShouldBe(network.Hops.Count);
        (await Count(db, "service")).ShouldBe(network.Services.Count);
        await using var conn = await db.OpenConnectionAsync(Ct);
        (await IntegrityCheck.RunAsync(conn, TextWriter.Null, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_to_load_over_an_existing_network_unless_reset()
    {
        await using var db = await factory.NewDatabaseAsync();
        var network = NetworkBuilder.Build(3, Scale.Small, TypeCatalog.Current);
        await Loader.LoadAsync(db, network, reset: false, TextWriter.Null, ct: Ct);

        await Should.ThrowAsync<InvalidOperationException>(() => Loader.LoadAsync(db, network, reset: false, TextWriter.Null, ct: Ct));
        await Loader.LoadAsync(db, network, reset: true, TextWriter.Null, ct: Ct);

        (await Count(db, "site")).ShouldBe(network.Sites.Count);
        await using var cmd = db.CreateCommand("SELECT nextval(pg_get_serial_sequence('terminal', 'id'))");
        ((long)(await cmd.ExecuteScalarAsync(Ct))!).ShouldBe(network.Terminals + 1);
    }

    [Fact]
    public async Task Loads_as_a_role_without_superuser()
    {
        // The demo database owner (CloudNativePG) is not superuser, so the fast path must fall back quietly.
        await using var admin = await factory.NewDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(admin.ConnectionString).Database;
        var role = $"owner_{Guid.NewGuid():N}";
        await using (var cmd = admin.CreateCommand(
            $"CREATE ROLE {role}; GRANT {role} TO CURRENT_USER; " +
            $"ALTER DATABASE {database} OWNER TO {role}; ALTER SCHEMA public OWNER TO {role}"))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        NpgsqlDataSource As(string? scopes) => Cmdb.Database.CmdbDatabase.CreateDataSource(new NpgsqlConnectionStringBuilder(factory.ConnectionString)
        {
            Database = database,
            Options = $"-c role={role}" + (scopes is null ? "" : $" -c cmdb.scopes={scopes}"),
        }.ConnectionString);
        await using var db = As("*"); // like the generator's command line
        var network = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current);

        await Loader.LoadAsync(db, network, reset: false, TextWriter.Null, ct: Ct);

        (await Count(db, "service")).ShouldBe(network.Services.Count);

    }

    [Fact]
    public async Task Seeds_demo_plans_including_two_that_want_the_same_fibre_termination()
    {
        await using var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);

        (await Count(db, "plan")).ShouldBe(5);
        (await Count(db, "reservation")).ShouldBe(1);
        await using var cmd = db.CreateCommand("""
            SELECT count(DISTINCT o.plan_id) FROM plan_operation o JOIN reservation r ON r.resource_kind = 'terminal'
            WHERE o.kind = 'connect' AND (o.payload->>'b')::bigint = r.resource_id
            """);
        ((long)(await cmd.ExecuteScalarAsync(Ct))!).ShouldBe(2);
    }

    [Fact]
    public async Task The_operations_agents_scenarios_are_seeded_the_same_when_run_again()
    {
        await using var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, scenarios: true, ct: Ct);
        var circuits = await Count(db, "circuit");

        await using (var conn = await db.OpenConnectionAsync(Ct))
        {
            await DemoScenarios.SeedAsync(conn, TextWriter.Null, Ct);
        }

        (await Count(db, "circuit")).ShouldBe(circuits);
        await using var cmd = db.CreateCommand($$"""
            SELECT (SELECT count(*) FROM site WHERE name = '{{DemoScenarios.Station}}'),
                   (SELECT count(*) FROM circuit WHERE code LIKE '%{{DemoScenarios.BackupSuffix}}'),
                   (SELECT count(*) FROM service s JOIN classification c ON c.object_type = 'service' AND c.object_id = s.id AND c.schema_key = 'criticality' AND c.level >= 5
                    WHERE s.name LIKE 'Mobilnät {{DemoScenarios.Station}}%')
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)).ShouldBe((1L, 2L, 2L));
    }

    [Theory]
    [InlineData(55.40, 13.35)]
    [InlineData(59.33, 18.07)]
    [InlineData(63.80, 20.40)]
    [InlineData(68.90, 20.60)]
    public async Task Projection_matches_PostGIS(double latitude, double longitude)
    {
        var (x, y) = SwerefTm.FromLatLon(latitude, longitude);

        await using var cmd = factory.Db.CreateCommand("SELECT ST_X(p), ST_Y(p) FROM (SELECT ST_Transform(ST_SetSRID(ST_MakePoint($1, $2), 4326), 3006) p) t");
        cmd.Parameters.Add(new NpgsqlParameter { Value = longitude });
        cmd.Parameters.Add(new NpgsqlParameter { Value = latitude });
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);

        x.ShouldBe(reader.GetDouble(0), tolerance: 0.001);
        y.ShouldBe(reader.GetDouble(1), tolerance: 0.001);
    }

    private static async Task<long> Count(NpgsqlDataSource db, string table)
    {
        await using var cmd = db.CreateCommand($"SELECT count(*) FROM {table}");
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

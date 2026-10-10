using Cmdb.Exchange;
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

    [Fact]
    public async Task Cables_lie_in_conduit_whose_route_segments_give_their_route_and_corridors_are_shared()
    {
        await using var db = await factory.NewDatabaseAsync();
        var network = NetworkBuilder.Build(1, Scale.Small, TypeCatalog.Current);
        await Loader.LoadAsync(db, network, reset: false, TextWriter.Null, fast: false, ct: Ct);
        await using var cmd = db.CreateCommand("""
            WITH way AS (
                SELECT p.cable_id, sum(r.length_m) AS length, count(*) AS segments
                FROM cable_path p JOIN subduct s ON s.id = p.subduct_id JOIN duct_segment ds ON ds.duct_id = s.duct_id
                JOIN route_segment r ON r.id = ds.route_segment_id GROUP BY p.cable_id
            ), shared AS (
                SELECT ds.route_segment_id FROM cable_path p JOIN subduct s ON s.id = p.subduct_id JOIN duct_segment ds ON ds.duct_id = s.duct_id
                JOIN cable c ON c.id = p.cable_id JOIN cable_type t ON t.id = c.cable_type_id
                GROUP BY ds.route_segment_id HAVING count(DISTINCT t.key) FILTER (WHERE t.key IN ('fiber-288', 'fiber-96')) = 2
            )
            SELECT (SELECT count(*) FROM cable),
                   (SELECT count(*) FROM way),
                   (SELECT count(*) FROM way w JOIN cable c ON c.id = w.cable_id WHERE abs(c.length_m - w.length) > 0.5),
                   (SELECT count(*) FROM shared),
                   (SELECT count(*) FROM subduct WHERE occupancy = 'cable') - (SELECT count(*) FROM cable_path),
                   (SELECT count(*) FROM site WHERE site_type = 'manhole'),
                   (SELECT count(*) FROM site s WHERE site_type = 'manhole'
                    AND NOT EXISTS (SELECT 1 FROM route_segment r WHERE s.id IN (r.a_site_id, r.b_site_id))),
                   (SELECT count(*) FROM route_segment WHERE trunk),
                   (SELECT count(*) FROM route_segment r WHERE r.trunk <> EXISTS (
                        SELECT 1 FROM duct_segment ds JOIN subduct s ON s.duct_id = ds.duct_id JOIN cable_path p ON p.subduct_id = s.id
                        JOIN cable c ON c.id = p.cable_id JOIN cable_type t ON t.id = c.cable_type_id
                        WHERE ds.route_segment_id = r.id AND t.conductor_count >= 96))
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        // Every cable is in conduit, and its route is its segments' routes.
        reader.GetInt64(1).ShouldBe(reader.GetInt64(0));
        reader.GetInt64(2).ShouldBe(0);
        // Backbone and rings share corridors; a tube with a cable is occupied; manholes lie on corridors.
        reader.GetInt64(3).ShouldBeGreaterThan(0);
        reader.GetInt64(4).ShouldBe(0);
        reader.GetInt64(5).ShouldBeGreaterThan(0);
        reader.GetInt64(6).ShouldBe(0);
        // Trunk conduit (#243) is exactly the conduit with a cable of 96 fibres or more.
        reader.GetInt64(7).ShouldBeGreaterThan(0);
        reader.GetInt64(8).ShouldBe(0);
        await reader.CloseAsync();
        await using var conn = await db.OpenConnectionAsync(Ct);
        (await IntegrityCheck.RunAsync(conn, TextWriter.Null, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Builds_and_loads_a_network_from_an_external_catalog_with_its_own_types()
    {
        // The test catalog (#207) names its site types and categories differently and has one switch, one ODF and one fibre (#219).
        var catalog = TypeCatalog.Load(CatalogSource.FromPath(Path.Combine(AppContext.BaseDirectory, "TestCatalog")));
        var network = NetworkBuilder.Build(1, Scale.Small, catalog);
        Fingerprint.Of(NetworkBuilder.Build(1, Scale.Small, catalog)).ShouldBe(Fingerprint.Of(network));
        network.Sites.Select(s => s.SiteType).Distinct().Order().ShouldBe(["brunn", "karna", "kundskap", "nod"]);
        network.Equipment.Select(e => e.Type.Key).Distinct().Order().ShouldBe(["globex-odf-8", "globex-sw-4"]);
        network.Cables.ShouldAllBe(c => c.Type.Key == "globex-fiber-8");
        network.Equipment.ShouldAllBe(e => catalog.ValidateAttributes(e.Type.Key, System.Text.Json.JsonDocument.Parse(e.Attributes, default).RootElement).Count == 0);

        await using var db = await factory.NewDatabaseAsync();
        // The demo scenarios find the hubs and aggregation nodes by role; without mobile backhaul services they step aside.
        await Loader.LoadAsync(db, network, reset: false, TextWriter.Null, fast: false, scenarios: true, ct: Ct);

        (await Count(db, "site")).ShouldBe(network.Sites.Count);
        (await Count(db, "connection")).ShouldBe(network.Connections.Count);
        await using var conn = await db.OpenConnectionAsync(Ct);
        (await IntegrityCheck.RunAsync(conn, TextWriter.Null, Ct)).ShouldBeEmpty();
        // Every access site has a service on a logical circuit that rides its link and its aggregation node's ring.
        await using var cmd = db.CreateCommand("""
            SELECT (SELECT count(*) FROM site WHERE site_type = 'kundskap'),
                   (SELECT count(DISTINCT sc.service_id) FROM service_circuit sc JOIN circuit c ON c.id = sc.circuit_id AND c.layer = 'logical'
                    WHERE (SELECT count(*) FROM circuit_dependency d WHERE d.circuit_id = c.id) = 2)
            """);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        reader.GetInt64(1).ShouldBe(reader.GetInt64(0));
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

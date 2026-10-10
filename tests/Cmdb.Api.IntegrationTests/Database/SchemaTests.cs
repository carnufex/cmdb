using Npgsql;

namespace Cmdb.Api.IntegrationTests.Database;

/// <summary>Constraints in the core schema. Each test runs in a transaction that is rolled back.</summary>
public sealed class SchemaTests(ApiFactory factory) : IAsyncLifetime
{
    private NpgsqlConnection _conn = null!;
    private NpgsqlTransaction _tx = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _conn = await factory.Db.OpenConnectionAsync();
        _tx = await _conn.BeginTransactionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _tx.RollbackAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Site_geometry_must_be_sweref99_tm()
    {
        var ex = await Should.ThrowAsync<PostgresException>(() =>
            Exec("INSERT INTO site (code, name, site_type, geom) VALUES ('S1', 'Site', 'core', ST_SetSRID(ST_MakePoint(18.0, 59.3), 4326))"));

        ex.Message.ShouldContain("SRID");
    }

    [Fact]
    public async Task Site_geometry_must_be_a_point_or_polygon()
    {
        var ex = await Should.ThrowAsync<PostgresException>(() =>
            Exec("INSERT INTO site (code, name, site_type, geom) VALUES ('S1', 'Site', 'core', 'SRID=3006;LINESTRING(0 0, 1 1)')"));

        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Cable_length_is_derived_from_its_geometry_in_metres()
    {
        await Exec("""
            INSERT INTO site (code, name, site_type, geom) VALUES
                ('A', 'A', 'core', 'SRID=3006;POINT(500000 6500000)'),
                ('B', 'B', 'core', 'SRID=3006;POINT(503000 6504000)');
            INSERT INTO cable_type (key, name, medium, conductor_count) VALUES ('f48', 'Fiber 48', 'fiber', 48);
            """);

        var length = await Scalar<double>("""
            INSERT INTO cable (cable_type_id, code, a_site_id, b_site_id, geom)
            SELECT ct.id, 'C1', a.id, b.id, 'SRID=3006;LINESTRING(500000 6500000, 503000 6504000)'
            FROM cable_type ct, site a, site b WHERE ct.key = 'f48' AND a.code = 'A' AND b.code = 'B'
            RETURNING length_m
            """);

        length.ShouldBe(5000, tolerance: 1e-9);
    }

    [Fact]
    public async Task A_terminal_is_either_a_port_or_a_conductor_end_never_both()
    {
        var terminal = await Scalar<long>("INSERT INTO terminal (kind) VALUES ('conductor_end') RETURNING id");
        var equipment = await NewEquipmentAsync();

        var ex = await Should.ThrowAsync<PostgresException>(() =>
            Exec($"INSERT INTO port (terminal_id, equipment_id, name, port_type, position) VALUES ({terminal}, {equipment}, 'ge-0/0/1', 'RJ45', 1)"));

        ex.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Equipment_sits_in_a_location_or_a_slot_not_both()
    {
        var chassis = await NewEquipmentAsync();

        var ex = await Should.ThrowAsync<PostgresException>(() => Exec($"""
            INSERT INTO equipment (equipment_type_id, site_id, location_id, parent_id, slot, name)
            SELECT equipment_type_id, site_id, location_id, id, '1', 'Card' FROM equipment WHERE id = {chassis}
            """));

        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Connections_are_stored_once_per_pair_with_a_below_b()
    {
        var a = await Scalar<long>("INSERT INTO terminal (kind) VALUES ('port') RETURNING id");
        var b = await Scalar<long>("INSERT INTO terminal (kind) VALUES ('port') RETURNING id");

        var reversed = await Should.ThrowAsync<PostgresException>(() =>
            Exec($"INSERT INTO connection (a_terminal_id, b_terminal_id, kind) VALUES ({b}, {a}, 'patch')"));
        reversed.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        await Exec($"INSERT INTO connection (a_terminal_id, b_terminal_id, kind) VALUES ({a}, {b}, 'patch')");
        var duplicate = await Should.ThrowAsync<PostgresException>(() =>
            Exec($"INSERT INTO connection (a_terminal_id, b_terminal_id, kind) VALUES ({a}, {b}, 'patch')"));
        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Conduit_holds_ducts_along_route_segments_with_one_cable_per_subduct()
    {
        // ADR-0014: a route segment between two sites, a duct along it, its subducts, and a cable's path through one.
        await Exec("""
            INSERT INTO site (code, name, site_type, geom) VALUES
                ('KA', 'A', 'manhole', 'SRID=3006;POINT(500000 6500000)'),
                ('KB', 'B', 'manhole', 'SRID=3006;POINT(500300 6500400)');
            INSERT INTO cable_type (key, name, medium, conductor_count) VALUES ('k12', 'Fiber 12', 'fiber', 12);
            INSERT INTO duct_type (key, name, manufacturer, model, outer_diameter_mm, subduct_count, subduct_inner_diameter_mm)
            VALUES ('md7', 'Multidukt', 'Acme', 'MD-7', 50, 7, 12);
            INSERT INTO route_segment (code, a_site_id, b_site_id, construction, geom)
            SELECT 'TR-1', a.id, b.id, 'trench', 'SRID=3006;LINESTRING(500000 6500000, 500300 6500400)' FROM site a, site b WHERE a.code = 'KA' AND b.code = 'KB';
            INSERT INTO duct (code, duct_type_id) SELECT 'D-1', id FROM duct_type WHERE key = 'md7';
            INSERT INTO duct_segment (duct_id, seq, route_segment_id) SELECT d.id, 0, r.id FROM duct d, route_segment r WHERE d.code = 'D-1' AND r.code = 'TR-1';
            INSERT INTO subduct (duct_id, number) SELECT d.id, n FROM duct d, generate_series(1, 7) n WHERE d.code = 'D-1';
            INSERT INTO cable (cable_type_id, code, a_site_id, b_site_id, geom)
            SELECT ct.id, 'C-1', a.id, b.id, 'SRID=3006;LINESTRING(500000 6500000, 500300 6500400)'
            FROM cable_type ct, site a, site b WHERE ct.key = 'k12' AND a.code = 'KA' AND b.code = 'KB';
            INSERT INTO cable_path (cable_id, seq, subduct_id)
            SELECT c.id, 0, s.id FROM cable c, subduct s JOIN duct d ON d.id = s.duct_id WHERE c.code = 'C-1' AND d.code = 'D-1' AND s.number = 1;
            UPDATE subduct SET occupancy = 'cable' WHERE number = 1 AND duct_id = (SELECT id FROM duct WHERE code = 'D-1');
            """);
        (await Scalar<double>("SELECT length_m FROM route_segment WHERE code = 'TR-1'")).ShouldBe(500, tolerance: 1e-9);
        (await Scalar<string>("SELECT occupancy FROM subduct s JOIN duct d ON d.id = s.duct_id WHERE d.code = 'D-1' AND s.number = 2")).ShouldBe("empty");

        // Duct in duct: a microduct bundle pulled into tube 2.
        await Exec("""
            INSERT INTO duct (code, duct_type_id, parent_subduct_id)
            SELECT 'D-2', t.id, s.id FROM duct_type t, subduct s JOIN duct d ON d.id = s.duct_id WHERE t.key = 'md7' AND d.code = 'D-1' AND s.number = 2
            """);

        async Task Refused(string sql, string state)
        {
            await Exec("SAVEPOINT s");
            (await Should.ThrowAsync<PostgresException>(() => Exec(sql))).SqlState.ShouldBe(state);
            await Exec("ROLLBACK TO SAVEPOINT s");
        }
        await Refused("""
            INSERT INTO cable_path (cable_id, seq, subduct_id)
            SELECT c.id, 1, s.id FROM cable c, subduct s JOIN duct d ON d.id = s.duct_id WHERE c.code = 'C-1' AND d.code = 'D-1' AND s.number = 1
            """, PostgresErrorCodes.UniqueViolation);
        await Refused("UPDATE subduct SET occupancy = 'full'", PostgresErrorCodes.CheckViolation);
        await Refused("UPDATE route_segment SET construction = 'tunnel'", PostgresErrorCodes.CheckViolation);
        // Lit is derived from circuits, never stored.
        await Exec("INSERT INTO conductor (cable_id, number, usage) SELECT id, 1, 'dark_fibre' FROM cable WHERE code = 'C-1'");
        await Refused("INSERT INTO conductor (cable_id, number, usage) SELECT id, 2, 'lit' FROM cable WHERE code = 'C-1'", PostgresErrorCodes.CheckViolation);
        await Refused("UPDATE route_segment SET b_site_id = a_site_id", PostgresErrorCodes.CheckViolation);
        await Refused("INSERT INTO subduct (duct_id, number) SELECT id, 1 FROM duct WHERE code = 'D-1'", PostgresErrorCodes.UniqueViolation);
    }

    private async Task<long> NewEquipmentAsync() => await Scalar<long>("""
        WITH s AS (INSERT INTO site (code, name, site_type, geom) VALUES ('S', 'S', 'core', 'SRID=3006;POINT(500000 6500000)') RETURNING id),
             l AS (INSERT INTO location (site_id, kind, name) SELECT id, 'rack', 'R1' FROM s RETURNING id, site_id),
             t AS (INSERT INTO equipment_type (key, manufacturer, model, category) VALUES ('sw', 'Testtillverkare', 'TT-48', 'switch') RETURNING id)
        INSERT INTO equipment (equipment_type_id, site_id, location_id, name)
        SELECT t.id, l.site_id, l.id, 'SW1' FROM t, l RETURNING id
        """);

    private async Task Exec(string sql)
    {
        await using var savepoint = new NpgsqlCommand("SAVEPOINT s", _conn, _tx);
        await savepoint.ExecuteNonQueryAsync(Ct);
        try
        {
            await using var cmd = new NpgsqlCommand(sql, _conn, _tx);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        catch (PostgresException)
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT s", _conn, _tx);
            await rollback.ExecuteNonQueryAsync(Ct);
            throw;
        }
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, _conn, _tx);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Cmdb.Catalog;
using Cmdb.Database;
using Npgsql;
using NpgsqlTypes;

namespace Cmdb.DataGen;

/// <summary>Writes a generated network with binary COPY. Ids come from the generator; sequences are moved past them afterwards.</summary>
internal static class Loader
{
    private static readonly string[] NetworkTables =
    [
        "classification", "service_circuit", "circuit_dependency", "circuit_hop", "circuit", "service", "channel",
        "connection", "conductor_end", "conductor", "cable", "port", "terminal", "equipment", "location", "site",
    ];

    private static readonly string[] IdentityTables =
        ["site", "location", "equipment", "terminal", "cable", "conductor", "connection", "channel", "service", "circuit"];

    public static async Task LoadAsync(NpgsqlDataSource db, Network net, bool reset, TextWriter log, bool fast = true, bool scenarios = false,
        CancellationToken ct = default)
    {
        // The schema is owned by the EF model (ADR-0009); the loader only migrates and then bulk-writes rows.
        await CmdbDatabase.MigrateAsync(db, ct);
        await using (var context = CmdbDatabase.CreateContext(db))
        {
            await CatalogSync.SyncAsync(context, net.Catalog, ct);
            await Cmdb.Database.Scopes.ScopeCatalog.SyncAsync(context, ct);
            await Cmdb.Database.Voice.VoiceCallerCatalog.SyncAsync(context, ct);
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        // Bulk rows skip the change stream's triggers (#11); one 'reload' entry at the end tells the graph to start over.
        await Exec(conn, "SET cmdb.bulk = 'on'", ct);
        await Exec(conn, "SET cmdb.scopes = '*'", ct);
        if (reset)
        {
            // Plans (#24) and reservations (#25) refer to terminals and objects by id, so they go with the network.
            await Exec(conn, $"TRUNCATE {string.Join(", ", NetworkTables)}, graph_change, reservation, plan_operation, plan_dependency, plan, incident, planned_work, voice_session, voice_challenge, voice_sms, voice_tool_call RESTART IDENTITY", ct);
        }
        else if (await Scalar<bool>(conn, "SELECT EXISTS (SELECT 1 FROM site)", ct))
        {
            throw new InvalidOperationException("The database already holds a network. Run with --reset to replace it.");
        }

        // Foreign keys are checked per row, which dominates load time at full scale. When the role may,
        // skip the checks for this session; the small-scale integration test loads with checks on.
        var checks = "on";
        try
        {
            if (fast)
            {
                await Exec(conn, "SET session_replication_role = replica", ct);
                checks = "off (session_replication_role = replica)";
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
        }
        await Exec(conn, "SET synchronous_commit = off", ct);
        log.WriteLine($"Foreign key checks during load: {checks}");

        var equipmentTypes = await Ids(conn, "SELECT key, id FROM equipment_type", ct);
        var cableTypes = await Ids(conn, "SELECT key, id FROM cable_type", ct);

        await Copy(conn, log, "site", "id, code, name, site_type, geom, lifecycle", net.Sites, (w, s) =>
        {
            w.Write(s.Id);
            w.Write(s.Code);
            w.Write(s.Name);
            w.Write(s.SiteType);
            w.Write(Point(s.X, s.Y), NpgsqlDbType.Bytea);
            w.Write(s.Lifecycle, NpgsqlDbType.Text);
        }, ct);

        await Copy(conn, log, "location", "id, site_id, parent_id, kind, name, rack_units, lifecycle", net.Locations, (w, l) =>
        {
            w.Write(l.Id);
            w.Write(l.SiteId);
            Nullable(w, l.ParentId);
            w.Write(l.Kind);
            w.Write(l.Name);
            if (l.RackUnits is { } units)
            {
                w.Write(units);
            }
            else
            {
                w.WriteNull();
            }
            w.Write(l.Lifecycle, NpgsqlDbType.Text);
        }, ct);

        await Copy(conn, log, "equipment", "id, equipment_type_id, site_id, location_id, parent_id, slot, name, attributes, lifecycle", net.Equipment, (w, e) =>
        {
            w.Write(e.Id);
            w.Write(equipmentTypes[e.Type.Key]);
            w.Write(e.SiteId);
            Nullable(w, e.LocationId);
            Nullable(w, e.ParentId);
            if (e.Slot is null)
            {
                w.WriteNull();
            }
            else
            {
                w.Write(e.Slot);
            }
            w.Write(e.Name);
            w.Write(e.Attributes, NpgsqlDbType.Jsonb);
            w.Write(e.Lifecycle, NpgsqlDbType.Text);
        }, ct);

        await Copy(conn, log, "terminal", "id, kind", Terminals(net), (w, t) =>
        {
            w.Write(t.Id);
            w.Write(t.Kind, NpgsqlDbType.Text);
        }, ct);

        await Copy(conn, log, "port", "terminal_id, equipment_id, name, port_type, port_group, position",
            net.Equipment.SelectMany(e => e.Ports.Select((p, i) => (Terminal: e.TerminalAt(i), Equipment: e.Id, Port: p))), (w, p) =>
            {
                w.Write(p.Terminal);
                w.Write(p.Equipment);
                w.Write(p.Port.Name);
                w.Write(p.Port.Type);
                if (p.Port.Group is null)
                {
                    w.WriteNull();
                }
                else
                {
                    w.Write(p.Port.Group);
                }
                w.Write(p.Port.Position);
            }, ct);

        await Copy(conn, log, "cable", "id, cable_type_id, code, a_site_id, b_site_id, geom, lifecycle", net.Cables, (w, c) =>
        {
            w.Write(c.Id);
            w.Write(cableTypes[c.Type.Key]);
            w.Write(c.Code);
            w.Write(c.A.Id);
            w.Write(c.B.Id);
            w.Write(LineString(c.Coordinates), NpgsqlDbType.Bytea);
            w.Write(c.Lifecycle, NpgsqlDbType.Text);
        }, ct);

        await Copy(conn, log, "conductor", "id, cable_id, number, color",
            net.Cables.SelectMany(c => Enumerable.Range(1, c.Count).Select(n => (Id: c.FirstConductor + n - 1, Cable: c.Id, Number: n))), (w, c) =>
            {
                w.Write(c.Id);
                w.Write(c.Cable);
                w.Write(c.Number);
                w.Write(NetworkBuilder.ConductorColor(c.Number));
            }, ct);

        await Copy(conn, log, "conductor_end", "terminal_id, conductor_id, side",
            net.Cables.SelectMany(c => Enumerable.Range(1, c.Count).SelectMany(n => new[]
            {
                (Terminal: c.End(n, c.A), Conductor: c.FirstConductor + n - 1, Side: "A"),
                (Terminal: c.End(n, c.B), Conductor: c.FirstConductor + n - 1, Side: "B"),
            })), (w, e) =>
            {
                w.Write(e.Terminal);
                w.Write(e.Conductor);
                w.Write(e.Side, NpgsqlDbType.Char);
            }, ct);

        await Copy(conn, log, "connection", "id, a_terminal_id, b_terminal_id, kind, lifecycle",
            net.Connections.Select((c, i) => (Id: i + 1L, Connection: c)), (w, c) =>
            {
                w.Write(c.Id);
                w.Write(c.Connection.A);
                w.Write(c.Connection.B);
                w.Write(c.Connection.Kind switch
                {
                    ConnectionKind.Patch => "patch",
                    ConnectionKind.Splice => "splice",
                    ConnectionKind.Termination => "termination",
                    _ => "internal",
                }, NpgsqlDbType.Text);
                w.Write(c.Connection.Planned ? Lifecycle.Planned : Lifecycle.InService, NpgsqlDbType.Text);
            }, ct);

        await Copy(conn, log, "channel", "id, terminal_id, kind, number", net.Channels, (w, c) =>
        {
            w.Write(c.Id);
            w.Write(c.TerminalId);
            w.Write(c.Kind, NpgsqlDbType.Text);
            w.Write(c.Number);
        }, ct);

        await Copy(conn, log, "service", "id, code, name, service_type, attributes, lifecycle", net.Services, (w, s) =>
        {
            w.Write(s.Id);
            w.Write(s.Code);
            w.Write(s.Name);
            w.Write(s.Type);
            w.Write(s.Attributes, NpgsqlDbType.Jsonb);
            w.Write(s.Lifecycle, NpgsqlDbType.Text);
        }, ct);

        await Copy(conn, log, "circuit", "id, code, layer, a_terminal_id, b_terminal_id, lifecycle", net.Circuits, (w, c) =>
        {
            w.Write(c.Id);
            w.Write(c.Code);
            w.Write(c.Layer, NpgsqlDbType.Text);
            w.Write(c.A);
            w.Write(c.B);
            w.Write(c.Lifecycle, NpgsqlDbType.Text);
        }, ct);

        await Copy(conn, log, "circuit_hop", "circuit_id, seq, terminal_id, channel_id", net.Hops, (w, h) =>
        {
            w.Write(h.CircuitId);
            w.Write(h.Seq);
            w.Write(h.TerminalId);
            Nullable(w, h.ChannelId);
        }, ct);

        await Copy(conn, log, "circuit_dependency", "circuit_id, carrier_id", net.Dependencies, (w, d) =>
        {
            w.Write(d.Circuit);
            w.Write(d.Carrier);
        }, ct);

        await Copy(conn, log, "service_circuit", "service_id, circuit_id", net.ServiceCircuits, (w, s) =>
        {
            w.Write(s.Service);
            w.Write(s.Circuit);
        }, ct);

        if (checks != "on")
        {
            // Setting the role at all needs superuser, so only reset it when it was changed.
            await Exec(conn, "SET session_replication_role = origin", ct);
            var violations = await IntegrityCheck.RunAsync(conn, log, ct);
            if (violations.Count > 0)
            {
                throw new InvalidOperationException("Loaded network violates foreign keys:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
            }
        }
        foreach (var table in IdentityTables)
        {
            await Exec(conn, $"SELECT setval(pg_get_serial_sequence('{table}', 'id'), GREATEST((SELECT max(id) FROM {table}), 1))", ct);
        }
        await Exec(conn, "SET cmdb.bulk = 'off'", ct);
        await Exec(conn, "INSERT INTO graph_change (kind, key) VALUES ('reload', 0)", ct);
        var sw = Stopwatch.StartNew();
        if (scenarios)
        {
            // The operations agent's demo scenarios (#132) change the generated network, so only the demo loads them.
            await DemoScenarios.SeedAsync(conn, log, ct, net.Catalog);
        }
        await Cmdb.Database.Scopes.ScopeVisibility.RefreshAsync(db, ct);
        log.WriteLine($"  scopes  {sw.Elapsed.TotalSeconds,6:0.0} s");
        await DemoPlans.SeedAsync(conn, log, ct, net.Catalog);
        sw.Restart();
        // Rack positions (#173): the generator places equipment in racks; stacking them gives each its units.
        await Exec(conn, Cmdb.Database.RackStacking.Backfill, ct);
        await Exec(conn, "ANALYZE", ct);
        log.WriteLine($"  analyze {sw.Elapsed.TotalSeconds,6:0.0} s");
    }

    private static IEnumerable<(long Id, string Kind)> Terminals(Network net)
    {
        foreach (var e in net.Equipment)
        {
            for (var i = 0; i < e.Ports.Count; i++)
            {
                yield return (e.TerminalAt(i), "port");
            }
        }
        foreach (var c in net.Cables)
        {
            for (var n = 0; n < 2 * c.Count; n++)
            {
                yield return (c.FirstEndTerminal + n, "conductor_end");
            }
        }
    }

    private static async Task Copy<T>(NpgsqlConnection conn, TextWriter log, string table, string columns, IEnumerable<T> rows, Action<NpgsqlBinaryImporter, T> write, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var importer = await conn.BeginBinaryImportAsync($"COPY {table} ({columns}) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var row in rows)
        {
            await importer.StartRowAsync(ct);
            write(importer, row);
        }
        var count = await importer.CompleteAsync(ct);
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {table,-20} {count,11:N0} rows {sw.Elapsed.TotalSeconds,6:0.0} s"));
    }

    private static void Nullable(NpgsqlBinaryImporter w, long? value)
    {
        if (value is { } v)
        {
            w.Write(v);
        }
        else
        {
            w.WriteNull();
        }
    }

    // EWKB with SRID 3006, which PostGIS reads directly in binary COPY.
    private const uint SridFlag = 0x20000000;

    private static byte[] Point(double x, double y)
    {
        var bytes = new byte[1 + 4 + 4 + 16];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), SridFlag | 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), 3006);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(9), x);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(17), y);
        return bytes;
    }

    private static byte[] LineString(double[] coordinates)
    {
        var points = coordinates.Length / 2;
        var bytes = new byte[1 + 4 + 4 + 4 + (coordinates.Length * 8)];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), SridFlag | 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), 3006);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9), (uint)points);
        for (var i = 0; i < coordinates.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(13 + (i * 8)), coordinates[i]);
        }
        return bytes;
    }

    private static async Task<Dictionary<string, long>> Ids(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids[reader.GetString(0)] = reader.GetInt64(1);
        }
        return ids;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

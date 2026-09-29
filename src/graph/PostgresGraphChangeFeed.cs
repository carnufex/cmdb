using System.Data;
using System.Globalization;
using Npgsql;

namespace Cmdb.Graph;

/// <summary>
/// The change stream from Postgres (#11): triggers write changed keys to <c>graph_change</c> in the writing transaction
/// and a statement trigger sends <c>NOTIFY graph_change</c>, delivered at commit.
/// <para>
/// The watermark is <c>{database oid}:{xid}</c>: every change from a transaction below the xid has been read. A read
/// takes rows from <c>[watermark, xmin)</c>, where xmin is the oldest transaction still running, so it only sees
/// finished transactions and never skips one that commits late. The current rows for the changed keys are read in the
/// same snapshot, which makes applying idempotent and independent of commit order.
/// </para>
/// </summary>
public sealed class PostgresGraphChangeFeed(NpgsqlDataSource db) : IGraphChangeFeed, IAsyncDisposable
{
    private const string Position = """
        SELECT (SELECT oid FROM pg_database WHERE datname = current_database())::text, pg_snapshot_xmin(pg_current_snapshot())::text
        """;

    private NpgsqlConnection? _listener;

    public Task<string> PositionAsync(CancellationToken ct) => CurrentPositionAsync(db, ct);

    /// <summary>The current position, for a full load: taken before it, so changes during the load are read again after.</summary>
    public static async Task<string> CurrentPositionAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(Position);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return $"{reader.GetString(0)}:{reader.GetString(1)}";
    }

    /// <summary>The transaction id part of a watermark, or null when it is not one of ours.</summary>
    public static ulong? Xid(string? watermark)
    {
        var colon = watermark?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        return colon > 0 && ulong.TryParse(watermark.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var xid) ? xid : null;
    }

    public async Task<GraphChangeBatch> ReadAsync(string watermark, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        string database, horizon;
        await using (var cmd = new NpgsqlCommand(Position, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            (database, horizon) = (reader.GetString(0), reader.GetString(1));
        }
        var next = $"{database}:{horizon}";
        var keys = new GraphKeys();
        var rows = new GraphData();
        var since = Xid(watermark);
        if (since is null || !watermark.StartsWith(database + ":", StringComparison.Ordinal))
        {
            // Another database, or a graph that predates the change stream: start over.
            return new GraphChangeBatch(next, Reload: true, keys, rows, 0);
        }

        var reload = false;
        var changes = 0;
        await using (var cmd = new NpgsqlCommand("SELECT kind, key FROM graph_change WHERE tx >= $1::text::xid8 AND tx < $2::text::xid8", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = since.Value.ToString(CultureInfo.InvariantCulture) });
            cmd.Parameters.Add(new NpgsqlParameter { Value = horizon });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                changes++;
                var key = reader.GetInt64(1);
                switch (reader.GetString(0))
                {
                    case "equipment": keys.Equipment.Add(key); break;
                    case "cable": keys.Cables.Add(key); break;
                    case "terminal": keys.Terminals.Add(key); break;
                    case "circuit": keys.Circuits.Add(key); break;
                    default: reload = true; break;
                }
            }
        }
        if (!reload && keys.Count > 0)
        {
            await ReadRowsAsync(conn, tx, keys, rows, ct);
        }
        await tx.CommitAsync(ct);
        return new GraphChangeBatch(next, reload, keys, rows, changes);
    }

    private static async Task ReadRowsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, GraphKeys keys, GraphData rows, CancellationToken ct)
    {
        long[] equipment = [.. keys.Equipment], cables = [.. keys.Cables], terminals = [.. keys.Terminals], circuits = [.. keys.Circuits];
        NpgsqlBatchCommand Query(string sql, long[] ids) => new(sql) { Parameters = { new() { Value = ids } } };
        await using var batch = new NpgsqlBatch(conn, tx)
        {
            BatchCommands =
            {
                Query("SELECT id, site_id FROM equipment WHERE id = ANY($1)", equipment),
                Query("SELECT terminal_id, equipment_id FROM port WHERE equipment_id = ANY($1)", equipment),
                Query($"SELECT id, {GraphLoader.L("lifecycle")} FROM cable WHERE id = ANY($1)", cables),
                Query("SELECT id, cable_id FROM conductor WHERE cable_id = ANY($1)", cables),
                Query("SELECT ce.terminal_id, ce.conductor_id FROM conductor_end ce JOIN conductor co ON co.id = ce.conductor_id WHERE co.cable_id = ANY($1)", cables),
                Query($"""
                    SELECT a_terminal_id, b_terminal_id, (array_position(enum_range(NULL::connection_kind), kind) - 1)::smallint, {GraphLoader.L("lifecycle")}
                    FROM connection WHERE valid_to IS NULL AND (a_terminal_id = ANY($1) OR b_terminal_id = ANY($1))
                    """, terminals),
                Query("SELECT id, (array_position(enum_range(NULL::circuit_layer), layer) - 1)::smallint FROM circuit WHERE id = ANY($1)", circuits),
                Query("SELECT circuit_id, terminal_id FROM circuit_hop WHERE circuit_id = ANY($1) ORDER BY circuit_id, seq", circuits),
                Query("SELECT circuit_id, carrier_id FROM circuit_dependency WHERE circuit_id = ANY($1)", circuits),
                Query("SELECT service_id, circuit_id FROM service_circuit WHERE circuit_id = ANY($1)", circuits),
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);
        await Read(reader, r => { rows.EquipmentIds.Add(r.GetInt64(0)); rows.EquipmentSites.Add(r.GetInt64(1)); }, ct);
        await Read(reader, r => { rows.PortTerminals.Add(r.GetInt64(0)); rows.PortEquipment.Add(r.GetInt64(1)); }, ct);
        await Read(reader, r => { rows.CableIds.Add(r.GetInt64(0)); rows.CableLifecycles.Add((byte)r.GetInt16(1)); }, ct);
        await Read(reader, r => { rows.ConductorIds.Add(r.GetInt64(0)); rows.ConductorCables.Add(r.GetInt64(1)); }, ct);
        await Read(reader, r => { rows.EndTerminals.Add(r.GetInt64(0)); rows.EndConductors.Add(r.GetInt64(1)); }, ct);
        await Read(reader, r =>
        {
            rows.ConnectionA.Add(r.GetInt64(0));
            rows.ConnectionB.Add(r.GetInt64(1));
            rows.ConnectionKinds.Add((byte)r.GetInt16(2));
            rows.ConnectionLifecycles.Add((byte)r.GetInt16(3));
        }, ct);
        await Read(reader, r => { rows.CircuitIds.Add(r.GetInt64(0)); rows.CircuitLayers.Add((byte)r.GetInt16(1)); }, ct);
        await Read(reader, r => { rows.HopCircuits.Add(r.GetInt64(0)); rows.HopTerminals.Add(r.GetInt64(1)); }, ct);
        await Read(reader, r => { rows.DependencyCircuits.Add(r.GetInt64(0)); rows.DependencyCarriers.Add(r.GetInt64(1)); }, ct);
        await Read(reader, r => { rows.ServiceCircuitServices.Add(r.GetInt64(0)); rows.ServiceCircuitCircuits.Add(r.GetInt64(1)); }, ct, last: true);
    }

    private static async Task Read(NpgsqlDataReader reader, Action<NpgsqlDataReader> row, CancellationToken ct, bool last = false)
    {
        while (await reader.ReadAsync(ct))
        {
            row(reader);
        }
        if (!last)
        {
            await reader.NextResultAsync(ct);
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            if (_listener is null)
            {
                _listener = await db.OpenConnectionAsync(ct);
                await using var listen = new NpgsqlCommand("LISTEN graph_change", _listener);
                await listen.ExecuteNonQueryAsync(ct);
            }
            await _listener.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // The listening connection broke; polling on the timeout keeps changes flowing until it is back.
            await DisposeAsync();
            await Task.Delay(timeout, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync();
            _listener = null;
        }
    }
}

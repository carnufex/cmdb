using Npgsql;

namespace Cmdb.Graph;

/// <summary>
/// A time to load the graph at (#302, ADR-0013 §6). <paramref name="Knowledge"/> is what the CMDB said then, from the versions
/// recorded at the time; otherwise it is what was in service then as known now, from valid time.
/// </summary>
public sealed record GraphTime(DateTimeOffset At, bool Knowledge);

/// <summary>Reads the graph's rows from Postgres with binary COPY, one table per connection in parallel.</summary>
public static class GraphLoader
{
    // Enum values as their position in the Postgres enum, which is also the C# enum value.
    internal static string L(string column) => $"(array_position(enum_range(NULL::lifecycle_state), {column}) - 1)::smallint";

    public static Task<Graph> LoadAsync(NpgsqlDataSource db, CancellationToken ct = default) => LoadAsync(db, null, ct);

    /// <summary>
    /// Loads the graph now, or at a time (#302). At a time, connections and cables are taken as they were valid then and, in
    /// the knowledge mode, as recorded then: the current row when it was recorded by then, otherwise the version in the
    /// table's history. Equipment keeps its recorded site. Hops, dependencies and service circuits have no history (#23) and
    /// are read as they are now. A graph at a time does not follow the change stream.
    /// </summary>
    public static async Task<Graph> LoadAsync(NpgsqlDataSource db, GraphTime? time, CancellationToken ct = default)
    {
        // Taken before reading: changes committed during the load are read again from the change stream (#11).
        var version = await PostgresGraphChangeFeed.CurrentPositionAsync(db, ct);
        var data = new GraphData();
        // COPY takes no parameters; the time is our own value, written as a literal.
        var t = time is null ? "" : $"'{time.At.UtcDateTime:yyyy-MM-ddTHH:mm:ss.ffffff}Z'::timestamptz";
        string Valid(string alias) => $"{alias}.valid_from <= {t} AND ({alias}.valid_to IS NULL OR {alias}.valid_to > {t})";
        // In the knowledge mode, the version of a row that was in force at t: the history's when the current one came later.
        string AsKnown(string table, string alias) => time is { Knowledge: true }
            ? $"LEFT JOIN LATERAL (SELECT * FROM {table}_history h WHERE h.id = {alias}.id AND h.sys_from <= {t} AND h.sys_to > {t} LIMIT 1) v ON true"
            : $"LEFT JOIN LATERAL (SELECT NULL::bigint AS id, NULL::bigint AS site_id, NULL::lifecycle_state AS lifecycle, NULL::timestamptz AS valid_from, NULL::timestamptz AS valid_to) v ON true";
        var equipment = time is { Knowledge: true }
            ? $"SELECT e.id, coalesce(v.site_id, e.site_id) FROM equipment e {AsKnown("equipment", "e")}"
            : "SELECT id, site_id FROM equipment";
        // A cable not in force at t is drawn as planned: its conductors stay, but carry nothing then.
        var cables = time is null
            ? $"SELECT id, {L("lifecycle")} FROM cable"
            : $"""
                SELECT c.id, CASE WHEN v.id IS NOT NULL THEN CASE WHEN {Valid("v")} THEN {L("v.lifecycle")} ELSE 0::smallint END
                                  WHEN {(time.Knowledge ? $"c.sys_from <= {t} AND " : "")}{Valid("c")} THEN {L("c.lifecycle")} ELSE 0::smallint END
                FROM cable c {AsKnown("cable", "c")}
                """;
        // Circuits in force at t; their hops, dependencies and services follow them.
        var circuits = time switch
        {
            null => "SELECT id, layer FROM circuit",
            { Knowledge: false } => $"SELECT c.id, c.layer FROM circuit c WHERE {Valid("c")}",
            _ => $"""
                SELECT c.id, c.layer FROM circuit c {AsKnown("circuit", "c")}
                WHERE CASE WHEN v.id IS NOT NULL THEN {Valid("v")} ELSE c.sys_from <= {t} AND {Valid("c")} END
                """,
        };
        var inForce = time is null ? "" : $" WHERE circuit_id IN (SELECT id FROM ({circuits}) x)";
        var connections = time switch
        {
            null => "FROM connection WHERE valid_to IS NULL",
            { Knowledge: false } => $"FROM connection c WHERE {Valid("c")}",
            _ => $"""
                FROM (SELECT a_terminal_id, b_terminal_id, kind, lifecycle, valid_from, valid_to FROM connection WHERE sys_from <= {t}
                      UNION ALL
                      SELECT a_terminal_id, b_terminal_id, kind, lifecycle, valid_from, valid_to FROM connection_history
                      WHERE sys_from <= {t} AND sys_to > {t}) c
                WHERE {Valid("c")}
                  AND EXISTS (SELECT 1 FROM terminal x WHERE x.id = c.a_terminal_id) AND EXISTS (SELECT 1 FROM terminal x WHERE x.id = c.b_terminal_id)
                """,
        };

        await Task.WhenAll(
            Export(db, "SELECT terminal_id, equipment_id FROM port", r =>
            {
                data.PortTerminals.Add(r.Read<long>());
                data.PortEquipment.Add(r.Read<long>());
            }, ct),
            Export(db, "SELECT terminal_id, conductor_id FROM conductor_end", r =>
            {
                data.EndTerminals.Add(r.Read<long>());
                data.EndConductors.Add(r.Read<long>());
            }, ct),
            Export(db, equipment, r =>
            {
                data.EquipmentIds.Add(r.Read<long>());
                data.EquipmentSites.Add(r.Read<long>());
            }, ct),
            Export(db, "SELECT id, cable_id FROM conductor", r =>
            {
                data.ConductorIds.Add(r.Read<long>());
                data.ConductorCables.Add(r.Read<long>());
            }, ct),
            Export(db, cables, r =>
            {
                data.CableIds.Add(r.Read<long>());
                data.CableLifecycles.Add((byte)r.Read<short>());
            }, ct),
            Export(db, $"""
                SELECT a_terminal_id, b_terminal_id,
                       (array_position(enum_range(NULL::connection_kind), kind) - 1)::smallint, {L("lifecycle")}
                {connections}
                """, r =>
            {
                data.ConnectionA.Add(r.Read<long>());
                data.ConnectionB.Add(r.Read<long>());
                data.ConnectionKinds.Add((byte)r.Read<short>());
                data.ConnectionLifecycles.Add((byte)r.Read<short>());
            }, ct),
            Export(db, $"SELECT id, (array_position(enum_range(NULL::circuit_layer), layer) - 1)::smallint FROM ({circuits}) c", r =>
            {
                data.CircuitIds.Add(r.Read<long>());
                data.CircuitLayers.Add((byte)r.Read<short>());
            }, ct),
            Export(db, $"SELECT circuit_id, terminal_id FROM circuit_hop{inForce} ORDER BY circuit_id, seq", r =>
            {
                data.HopCircuits.Add(r.Read<long>());
                data.HopTerminals.Add(r.Read<long>());
            }, ct),
            Export(db, $"SELECT circuit_id, carrier_id FROM circuit_dependency{inForce}{(time is null ? "" : $" AND carrier_id IN (SELECT id FROM ({circuits}) x)")}", r =>
            {
                data.DependencyCircuits.Add(r.Read<long>());
                data.DependencyCarriers.Add(r.Read<long>());
            }, ct),
            Export(db, $"SELECT service_id, circuit_id FROM service_circuit{inForce}", r =>
            {
                data.ServiceCircuitServices.Add(r.Read<long>());
                data.ServiceCircuitCircuits.Add(r.Read<long>());
            }, ct));

        return GraphBuilder.Build(data, version);
    }

    private static async Task Export(NpgsqlDataSource db, string query, Action<NpgsqlBinaryExporter> readRow, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var export = await conn.BeginBinaryExportAsync($"COPY ({query}) TO STDOUT (FORMAT BINARY)", ct);
        while (await export.StartRowAsync(ct) != -1)
        {
            readRow(export);
        }
    }
}

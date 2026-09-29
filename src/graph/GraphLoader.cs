using Npgsql;

namespace Cmdb.Graph;

/// <summary>Reads the graph's rows from Postgres with binary COPY, one table per connection in parallel.</summary>
public static class GraphLoader
{
    // Enum values as their position in the Postgres enum, which is also the C# enum value.
    private static string L(string column) => $"(array_position(enum_range(NULL::lifecycle_state), {column}) - 1)::smallint";

    public static async Task<Graph> LoadAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        var version = await DataVersionAsync(db, ct);
        var data = new GraphData();

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
            Export(db, "SELECT id, site_id FROM equipment", r =>
            {
                data.EquipmentIds.Add(r.Read<long>());
                data.EquipmentSites.Add(r.Read<long>());
            }, ct),
            Export(db, "SELECT id, cable_id FROM conductor", r =>
            {
                data.ConductorIds.Add(r.Read<long>());
                data.ConductorCables.Add(r.Read<long>());
            }, ct),
            Export(db, $"SELECT id, {L("lifecycle")} FROM cable", r =>
            {
                data.CableIds.Add(r.Read<long>());
                data.CableLifecycles.Add((byte)r.Read<short>());
            }, ct),
            Export(db, $"""
                SELECT a_terminal_id, b_terminal_id,
                       (array_position(enum_range(NULL::connection_kind), kind) - 1)::smallint, {L("lifecycle")}
                FROM connection WHERE valid_to IS NULL
                """, r =>
            {
                data.ConnectionA.Add(r.Read<long>());
                data.ConnectionB.Add(r.Read<long>());
                data.ConnectionKinds.Add((byte)r.Read<short>());
                data.ConnectionLifecycles.Add((byte)r.Read<short>());
            }, ct),
            Export(db, "SELECT id, (array_position(enum_range(NULL::circuit_layer), layer) - 1)::smallint FROM circuit", r =>
            {
                data.CircuitIds.Add(r.Read<long>());
                data.CircuitLayers.Add((byte)r.Read<short>());
            }, ct),
            Export(db, "SELECT circuit_id, terminal_id FROM circuit_hop ORDER BY circuit_id, seq", r =>
            {
                data.HopCircuits.Add(r.Read<long>());
                data.HopTerminals.Add(r.Read<long>());
            }, ct),
            Export(db, "SELECT circuit_id, carrier_id FROM circuit_dependency", r =>
            {
                data.DependencyCircuits.Add(r.Read<long>());
                data.DependencyCarriers.Add(r.Read<long>());
            }, ct),
            Export(db, "SELECT service_id, circuit_id FROM service_circuit", r =>
            {
                data.ServiceCircuitServices.Add(r.Read<long>());
                data.ServiceCircuitCircuits.Add(r.Read<long>());
            }, ct));

        return GraphBuilder.Build(data, version);
    }

    /// <summary>
    /// A cheap fingerprint of the data the graph is built from: row counts and highest ids. Until the change stream
    /// (#11) gives a real sequence, this decides whether a snapshot file is still valid.
    /// </summary>
    public static async Task<string> DataVersionAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT concat_ws('.',
                (SELECT count(*) FROM terminal), (SELECT max(id) FROM terminal),
                (SELECT count(*) FROM connection WHERE valid_to IS NULL), (SELECT max(id) FROM connection),
                (SELECT count(*) FROM equipment), (SELECT max(id) FROM equipment),
                (SELECT count(*) FROM circuit_hop), (SELECT max(id) FROM circuit),
                (SELECT count(*) FROM circuit_dependency), (SELECT count(*) FROM service_circuit))
            """);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
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

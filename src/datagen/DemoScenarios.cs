using Npgsql;

namespace Cmdb.DataGen;

/// <summary>
/// The operations agent's demo scenarios (#132, docs/demo-scenarier.md), picked deterministically from the loaded
/// network and safe to run again on a loaded database (<c>--scenarios</c>):
/// <list type="bullet">
/// <item>the aggregation station most physical circuits pass through becomes "Lingonåsen", with aliases a caller might use;</item>
/// <item>a critical service through it gets a backup circuit that also runs through the station (false redundancy);</item>
/// <item>a second critical service gets a backup around the station (working redundancy);</item>
/// <item>a non-critical service through it keeps its single path;</item>
/// <item>transmission services (core links) are critical;</item>
/// <item>digging is planned across the cable that carries the most critical services (#137);</item>
/// <item>a backup battery at the station is past its lifetime (#137).</item>
/// </list>
/// </summary>
public static class DemoScenarios
{
    public const string Station = "Lingonåsen";
    public const string BackupSuffix = "-RESERV";
    public const string Contractor = "Markentreprenad Exempel AB";
    public const int BatteryYear = 2013;

    public static async Task SeedAsync(NpgsqlConnection conn, TextWriter log, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Run again: take the previous backups away first.
        await ExecAsync(conn, $"""
            CREATE TEMP TABLE old_backup ON COMMIT DROP AS SELECT id FROM circuit WHERE code LIKE '%{BackupSuffix}';
            DELETE FROM service_circuit WHERE circuit_id IN (SELECT id FROM old_backup);
            DELETE FROM circuit_dependency WHERE circuit_id IN (SELECT id FROM old_backup);
            DELETE FROM circuit_hop WHERE circuit_id IN (SELECT id FROM old_backup);
            DELETE FROM circuit WHERE id IN (SELECT id FROM old_backup);
            """, ct);

        // Physical circuits and the sites they pass between their ends.
        await ExecAsync(conn, """
            CREATE TEMP TABLE passes ON COMMIT DROP AS
            SELECT DISTINCT h.circuit_id, e.site_id
            FROM (SELECT circuit_id, terminal_id, seq, max(seq) OVER (PARTITION BY circuit_id) AS last FROM circuit_hop) h
            JOIN circuit c ON c.id = h.circuit_id AND c.layer = 'physical'
            JOIN port p ON p.terminal_id = h.terminal_id
            JOIN equipment e ON e.id = p.equipment_id
            WHERE h.seq > 0 AND h.seq < h.last;
            CREATE INDEX ON passes (site_id);
            CREATE INDEX ON passes (circuit_id);
            """, ct);
        var station = await RowAsync(conn, """
            SELECT s.id, s.code FROM passes x JOIN site s ON s.id = x.site_id AND s.site_type = 'aggregation'
            GROUP BY s.id, s.code ORDER BY count(*) DESC, s.id LIMIT 1
            """, ct);
        if (station is null)
        {
            return;
        }
        var (stationId, stationCode) = ((long)station[0], (string)station[1]);
        var alias = stationCode.Replace("AGG-", "", StringComparison.Ordinal).TrimStart('0');
        await ExecAsync(conn, $$"""
            UPDATE site SET name = '{{Station}}',
                attributes = attributes || '{"aliases": ["Lingonåsen station", "Lingonåsens nod", "LGÅ", "Lingon", "aggregering {{alias}}"]}'::jsonb
            WHERE id = {{stationId}}
            """, ct);

        // Services on a logical circuit that rides on a physical circuit through the station.
        var through = $"""
            SELECT s.id AS service_id, s.code, s.service_type, sc.circuit_id AS logical_id, d.carrier_id
            FROM service s
            JOIN service_circuit sc ON sc.service_id = s.id
            JOIN circuit l ON l.id = sc.circuit_id AND l.layer = 'logical'
            JOIN circuit_dependency d ON d.circuit_id = l.id
            JOIN passes x ON x.circuit_id = d.carrier_id AND x.site_id = {stationId}
            WHERE s.lifecycle = 'in_service'
            """;
        var falseBackup = await RowAsync(conn, $"SELECT service_id, logical_id, code FROM ({through}) t WHERE service_type = 'mobile-backhaul' ORDER BY service_id LIMIT 1", ct);
        var trueBackup = await RowAsync(conn, $"SELECT service_id, logical_id, code FROM ({through}) t WHERE service_type = 'mobile-backhaul' ORDER BY service_id OFFSET 1 LIMIT 1", ct);
        var single = await RowAsync(conn, $"SELECT service_id, logical_id, code FROM ({through}) t WHERE service_type = 'ethernet' ORDER BY service_id LIMIT 1", ct);
        if (falseBackup is null || trueBackup is null)
        {
            log.WriteLine("  scenarios: no mobile backhaul through the station, skipped");
            return;
        }

        // The false backup rides on another physical circuit through the station; the true one on one that avoids it.
        var sameStation = await RowAsync(conn, $"""
            SELECT x.circuit_id FROM passes x
            WHERE x.site_id = {stationId}
              AND x.circuit_id NOT IN (SELECT carrier_id FROM circuit_dependency WHERE circuit_id = {falseBackup[1]})
            ORDER BY x.circuit_id LIMIT 1
            """, ct);
        var elsewhere = await RowAsync(conn, $"""
            SELECT c.id FROM circuit c
            WHERE c.layer = 'physical' AND c.lifecycle = 'in_service'
              AND NOT EXISTS (SELECT 1 FROM circuit_hop h JOIN port p ON p.terminal_id = h.terminal_id
                              JOIN equipment e ON e.id = p.equipment_id WHERE h.circuit_id = c.id AND e.site_id = {stationId})
            ORDER BY c.id LIMIT 1
            """, ct);
        if (sameStation is null || elsewhere is null)
        {
            return;
        }
        await BackupAsync(conn, (long)falseBackup[0], (string)falseBackup[2], (long)sameStation[0], ct);
        await BackupAsync(conn, (long)trueBackup[0], (string)trueBackup[2], (long)elsewhere[0], ct);

        await ExecAsync(conn, $$"""
            UPDATE service SET attributes = attributes - 'criticality';
            UPDATE service SET attributes = attributes || '{"criticality": "critical"}'::jsonb
            WHERE service_type = 'core-link' OR id IN ({{falseBackup[0]}}, {{trueBackup[0]}});
            UPDATE service SET name = 'Mobilnät {{Station}} norr' WHERE id = {{falseBackup[0]}};
            UPDATE service SET name = 'Mobilnät {{Station}} syd' WHERE id = {{trueBackup[0]}};
            """, ct);
        if (single is not null)
        {
            await ExecAsync(conn, $"UPDATE service SET name = 'Företagsanslutning {Station}' WHERE id = {single[0]}", ct);
        }

        // Scenario 2: digging across the cable that carries the most critical services, starting in two days.
        await ExecAsync(conn, $"DELETE FROM planned_work WHERE contractor = '{Contractor}'", ct);
        await ExecAsync(conn, $$"""
            WITH RECURSIVE down(service_id, circuit_id) AS (
                SELECT sc.service_id, sc.circuit_id FROM service_circuit sc JOIN service s ON s.id = sc.service_id
                WHERE s.attributes->>'criticality' = 'critical'
                UNION
                SELECT d.service_id, cd.carrier_id FROM down d JOIN circuit_dependency cd ON cd.circuit_id = d.circuit_id
            ), busiest AS (
                SELECT k.cable_id FROM down d
                JOIN circuit_hop h ON h.circuit_id = d.circuit_id
                JOIN conductor_end ce ON ce.terminal_id = h.terminal_id
                JOIN conductor k ON k.id = ce.conductor_id
                JOIN cable c ON c.id = k.cable_id AND c.lifecycle = 'in_service'
                GROUP BY k.cable_id ORDER BY count(DISTINCT d.service_id) DESC, k.cable_id LIMIT 1
            )
            INSERT INTO planned_work (title, description, contractor, responsible_employee_id, area, starts_at, ends_at)
            SELECT 'Schaktning för fjärrvärmeledning',
                   'Grävning med maskin, 2 meter djup, längs en gång- och cykelväg.',
                   '{{Contractor}}', '1001',
                   ST_Buffer(ST_LineInterpolatePoint(c.geom, 0.5), 25),
                   date_trunc('day', now()) + interval '2 days 7 hours', date_trunc('day', now()) + interval '9 days 16 hours'
            FROM busiest b JOIN cable c ON c.id = b.cable_id
            """, ct);

        // Scenario 3: a backup battery at the station past its lifetime (8 years).
        await ExecAsync(conn, $$"""
            UPDATE equipment SET attributes = attributes - 'installationYear' WHERE attributes ? 'installationYear';
            UPDATE equipment SET attributes = attributes || '{"installationYear": {{BatteryYear}}}'::jsonb
            WHERE id = (SELECT e.id FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id AND t.key = 'acme-bat-100'
                        ORDER BY e.site_id <> {{stationId}}, e.id LIMIT 1);
            """, ct);
        await tx.CommitAsync(ct);
        log.WriteLine($"  scenarios  {Station} = {stationCode}, false backup {falseBackup[2]}, backup {trueBackup[2]}");
    }

    /// <summary>A second logical circuit for the service, on the given physical carrier and along its hops.</summary>
    private static Task BackupAsync(NpgsqlConnection conn, long service, string serviceCode, long carrier, CancellationToken ct) =>
        ExecAsync(conn, $"""
            WITH ends AS (
                SELECT (array_agg(terminal_id ORDER BY seq))[1] AS a, (array_agg(terminal_id ORDER BY seq DESC))[1] AS b
                FROM circuit_hop WHERE circuit_id = {carrier}
            ), c AS (
                INSERT INTO circuit (code, layer, a_terminal_id, b_terminal_id, lifecycle)
                SELECT '{serviceCode}{BackupSuffix}', 'logical', a, b, 'in_service' FROM ends
                RETURNING id
            ), h AS (
                INSERT INTO circuit_hop (circuit_id, seq, terminal_id)
                SELECT c.id, h.seq, h.terminal_id FROM c, circuit_hop h WHERE h.circuit_id = {carrier}
            ), d AS (
                INSERT INTO circuit_dependency (circuit_id, carrier_id) SELECT id, {carrier} FROM c
            )
            INSERT INTO service_circuit (service_id, circuit_id) SELECT {service}, id FROM c
            """, ct);

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object[]?> RowAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var row = new object[reader.FieldCount];
        reader.GetValues(row);
        return row;
    }
}

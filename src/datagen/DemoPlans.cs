using Npgsql;

namespace Cmdb.DataGen;

/// <summary>
/// Synthetic plans for the demo (#24), picked deterministically from the loaded network: a patching job at the first
/// hub, the decommissioning of a cable, and a second stage that builds on the first plan.
/// </summary>
internal static class DemoPlans
{
    private const string Author = "datagen";

    public static async Task SeedAsync(NpgsqlConnection conn, TextWriter log, CancellationToken ct)
    {
        var hub = await RowAsync(conn, "SELECT id, code, name FROM site WHERE site_type = 'hub' ORDER BY id LIMIT 1", ct);
        if (hub is null)
        {
            return;
        }
        var (hubId, hubCode, hubName) = ((long)hub[0], (string)hub[1], (string)hub[2]);
        var free = await LongsAsync(conn, $"""
            SELECT p.terminal_id FROM port p JOIN equipment e ON e.id = p.equipment_id
            WHERE e.site_id = {hubId}
              AND NOT EXISTS (SELECT 1 FROM connection c WHERE c.valid_to IS NULL AND p.terminal_id IN (c.a_terminal_id, c.b_terminal_id))
            ORDER BY p.terminal_id LIMIT 12
            """, ct);

        var patching = await PlanAsync(conn, $"Kapacitetsutbyggnad {hubCode}",
            $"Nya korskopplingar på {hubName} inför fler transmissionslänkar.", ct);
        for (var i = 0; i + 1 < Math.Min(free.Count, 8); i += 2)
        {
            await OperationAsync(conn, patching, "connect", $$"""{"a": {{free[i]}}, "b": {{free[i + 1]}}, "kind": "patch"}""", ct);
        }

        var cable = await RowAsync(conn, """
            SELECT c.id, c.code FROM cable c
            WHERE c.lifecycle = 'in_service'
              AND EXISTS (SELECT 1 FROM conductor k JOIN conductor_end ce ON ce.conductor_id = k.id
                          JOIN connection x ON x.valid_to IS NULL AND ce.terminal_id IN (x.a_terminal_id, x.b_terminal_id)
                          WHERE k.cable_id = c.id)
            ORDER BY c.id LIMIT 1
            """, ct);
        if (cable is not null)
        {
            var (cableId, cableCode) = ((long)cable[0], (string)cable[1]);
            var retire = await PlanAsync(conn, $"Avveckla {cableCode}", $"Kabeln {cableCode} tas ur drift och skarvarna i ändarna bryts.", ct);
            await OperationAsync(conn, retire, "set_lifecycle", $$"""{"type": "cable", "id": {{cableId}}, "lifecycle": "decommissioning"}""", ct);
            await using var cmd = new NpgsqlCommand($"""
                SELECT x.a_terminal_id, x.b_terminal_id FROM conductor k JOIN conductor_end ce ON ce.conductor_id = k.id
                JOIN connection x ON x.valid_to IS NULL AND ce.terminal_id IN (x.a_terminal_id, x.b_terminal_id)
                WHERE k.cable_id = {cableId} ORDER BY x.id LIMIT 4
                """, conn);
            var pairs = new List<(long, long)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    pairs.Add((reader.GetInt64(0), reader.GetInt64(1)));
                }
            }
            foreach (var (a, b) in pairs)
            {
                await OperationAsync(conn, retire, "disconnect", $$"""{"a": {{a}}, "b": {{b}}}""", ct);
            }
        }

        var stage2 = await PlanAsync(conn, $"Kapacitetsutbyggnad {hubCode}, etapp 2", "Fler korskopplingar och nytt namn på noden.", ct);
        await ExecAsync(conn, $"INSERT INTO plan_dependency (plan_id, depends_on_id) VALUES ({stage2}, {patching})", ct);
        await OperationAsync(conn, stage2, "rename", $$"""{"type": "site", "id": {{hubId}}, "name": "{{hubName}} (utbyggd)"}""", ct);
        if (free.Count >= 10)
        {
            await OperationAsync(conn, stage2, "connect", $$"""{"a": {{free[8]}}, "b": {{free[9]}}, "kind": "patch"}""", ct);
        }
        log.WriteLine("  plans   3 demo plans");
    }

    private static async Task<long> PlanAsync(NpgsqlConnection conn, string name, string description, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO plan (name, description, created_by) VALUES ($1, $2, $3) RETURNING id", conn);
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { Value = description });
        cmd.Parameters.Add(new() { Value = Author });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task OperationAsync(NpgsqlConnection conn, long plan, string kind, string payload, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO plan_operation (plan_id, seq, kind, payload, created_by)
            SELECT $1, coalesce(max(seq), 0) + 1, $2, $3::jsonb, $4 FROM plan_operation WHERE plan_id = $1
            """, conn);
        cmd.Parameters.Add(new() { Value = plan });
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = payload });
        cmd.Parameters.Add(new() { Value = Author });
        await cmd.ExecuteNonQueryAsync(ct);
    }

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

    private static async Task<List<long>> LongsAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<long>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(reader.GetInt64(0));
        }
        return list;
    }
}

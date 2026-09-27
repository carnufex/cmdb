using System.Diagnostics;
using Npgsql;

namespace Cmdb.DataGen;

/// <summary>
/// Verifies every foreign key in the schema with an anti-join. Used after a load that ran with triggers off,
/// so a fast load never leaves dangling references behind.
/// </summary>
internal static class IntegrityCheck
{
    public static async Task<IReadOnlyList<string>> RunAsync(NpgsqlConnection conn, TextWriter log, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var keys = new List<(string Name, string Child, string[] ChildColumns, string Parent, string[] ParentColumns)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.conname, c.conrelid::regclass::text, c.confrelid::regclass::text,
                   array(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY k(n, i) JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.n ORDER BY k.i),
                   array(SELECT a.attname FROM unnest(c.confkey) WITH ORDINALITY k(n, i) JOIN pg_attribute a ON a.attrelid = c.confrelid AND a.attnum = k.n ORDER BY k.i)
            FROM pg_constraint c
            WHERE c.contype = 'f' AND c.connamespace = 'public'::regnamespace
            ORDER BY 1
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                keys.Add((reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(3), reader.GetString(2), reader.GetFieldValue<string[]>(4)));
            }
        }

        var violations = new List<string>();
        foreach (var key in keys)
        {
            var join = string.Join(" AND ", key.ChildColumns.Zip(key.ParentColumns, (c, p) => $"p.{p} = c.{c}"));
            var notNull = string.Join(" AND ", key.ChildColumns.Select(c => $"c.{c} IS NOT NULL"));
            await using var cmd = new NpgsqlCommand(
                $"SELECT count(*) FROM {key.Child} c WHERE {notNull} AND NOT EXISTS (SELECT 1 FROM {key.Parent} p WHERE {join})", conn)
            {
                CommandTimeout = 0,
            };
            var dangling = (long)(await cmd.ExecuteScalarAsync(ct))!;
            if (dangling > 0)
            {
                violations.Add($"{key.Name}: {dangling} row(s) in {key.Child} without a matching {key.Parent}");
            }
        }
        log.WriteLine($"  integrity {keys.Count} foreign keys checked in {sw.Elapsed.TotalSeconds:0.0} s, {violations.Count} violated");
        return violations;
    }
}

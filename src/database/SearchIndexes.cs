using Npgsql;

namespace Cmdb.Database;

/// <summary>
/// The GIN indexes (trigram search, attribute paths) after a bulk write (#245). Rows written in bulk land in them one by one,
/// which leaves them two to five times larger than a fresh build; the quick search's trigram scans then take several times
/// longer (demo, full scale: 26 ms against 4 ms for one scan, search p95 42 against 19 ms). Rebuilding them packs them again.
/// </summary>
public static class SearchIndexes
{
    /// <summary>
    /// Rebuilds every GIN index in the schema. <paramref name="concurrently"/> keeps the tables readable and writable, for a
    /// database in use; it cannot run inside a transaction.
    /// </summary>
    public static async Task<int> RebuildAsync(NpgsqlConnection conn, bool concurrently, CancellationToken ct = default)
    {
        var names = new List<string>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT quote_ident(c.relname) FROM pg_class c JOIN pg_am a ON a.oid = c.relam JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE a.amname = 'gin' AND n.nspname = 'public' ORDER BY c.relname
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                names.Add(reader.GetString(0));
            }
        }
        foreach (var name in names)
        {
            await using var reindex = new NpgsqlCommand($"REINDEX INDEX {(concurrently ? "CONCURRENTLY " : "")}{name}", conn) { CommandTimeout = 0 };
            await reindex.ExecuteNonQueryAsync(ct);
        }
        return names.Count;
    }
}

using System.Globalization;
using Npgsql;

namespace Cmdb.Database;

/// <summary>
/// The operation log, history and read log (#23, ADR-0006, ADR-0013): which tables keep history, and the partitions the
/// logs are written to. Operations and history are partitioned by month, the read log by day; every partitioned table also
/// has a default partition, so a partition made late never stops a write (it takes over the rows the default caught).
/// </summary>
public static class OperationLog
{
    /// <summary>The tables whose earlier versions are kept in <c>&lt;table&gt;_history</c>.</summary>
    public static readonly IReadOnlyList<string> HistoryTables =
        ["site", "location", "equipment", "cable", "connection", "circuit", "service", "route_segment", "duct"];

    /// <summary>Makes the monthly and daily partitions from <paramref name="now"/> ahead; returns how many were made.</summary>
    public static async Task<int> EnsurePartitionsAsync(NpgsqlDataSource db, DateTimeOffset now, int monthsAhead = 2, int daysAhead = 3,
        CancellationToken ct = default)
    {
        var made = 0;
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i <= monthsAhead; i++)
        {
            var low = month.AddMonths(i);
            var suffix = low.ToString("'y'yyyy'm'MM", CultureInfo.InvariantCulture);
            made += await EnsureAsync(db, "operation", $"operation_{suffix}", "at", low, low.AddMonths(1), ct);
            foreach (var table in HistoryTables)
            {
                made += await EnsureAsync(db, $"{table}_history", $"{table}_history_{suffix}", "sys_to", low, low.AddMonths(1), ct);
            }
        }
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        for (var i = 0; i <= daysAhead; i++)
        {
            var low = day.AddDays(i);
            made += await EnsureAsync(db, "read_log", ReadLogPartition(low), "at", low, low.AddDays(1), ct);
        }
        return made;
    }

    /// <summary>The read log's daily partitions that end before <paramref name="before"/>, oldest first.</summary>
    public static async Task<IReadOnlyList<(string Name, DateTimeOffset Day)>> ReadLogPartitionsBeforeAsync(NpgsqlDataSource db,
        DateTimeOffset before, CancellationToken ct = default)
    {
        var parts = new List<(string, DateTimeOffset)>();
        await using var cmd = db.CreateCommand("""
            SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = 'read_log'::regclass AND c.relname LIKE 'read\_log\_d%' ORDER BY c.relname
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (DateTimeOffset.TryParseExact(name["read_log_d".Length..], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var day) && day.AddDays(1) <= before)
            {
                parts.Add((name, day));
            }
        }
        return parts;
    }

    public static string ReadLogPartition(DateTimeOffset day) => "read_log_d" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static async Task<int> EnsureAsync(NpgsqlDataSource db, string parent, string part, string key, DateTimeOffset low,
        DateTimeOffset high, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cmdb_ensure_partition($1, $2, $3, $4, $5)");
        cmd.Parameters.Add(new() { Value = parent });
        cmd.Parameters.Add(new() { Value = part });
        cmd.Parameters.Add(new() { Value = key });
        cmd.Parameters.Add(new() { Value = low });
        cmd.Parameters.Add(new() { Value = high });
        return (bool)(await cmd.ExecuteScalarAsync(ct))! ? 1 : 0;
    }
}

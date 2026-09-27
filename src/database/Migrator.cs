using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Cmdb.Database;

/// <summary>A versioned SQL migration embedded in this assembly.</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    public string Checksum { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
}

/// <summary>
/// Applies the embedded <c>Migrations/NNNN_name.sql</c> files in order, each in its own transaction,
/// under a session advisory lock so concurrent pods do not race. See ADR-0009.
/// </summary>
public static class Migrator
{
    // Arbitrary but fixed key for pg_advisory_lock.
    private const long LockKey = 0x434D_4442_4D49_4752;

    public static IReadOnlyList<Migration> Embedded { get; } = Load(typeof(Migrator).Assembly);

    /// <summary>Applies pending migrations and returns the versions that were applied.</summary>
    public static async Task<IReadOnlyList<int>> MigrateAsync(NpgsqlDataSource db, CancellationToken ct = default) =>
        await MigrateAsync(db, Embedded, ct);

    public static async Task<IReadOnlyList<int>> MigrateAsync(
        NpgsqlDataSource db, IReadOnlyList<Migration> migrations, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await Exec(conn, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            await Exec(conn, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    integer PRIMARY KEY,
                    name       text NOT NULL,
                    checksum   text NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """, ct);

            var applied = await AppliedAsync(conn, ct);
            var result = new List<int>();
            foreach (var m in migrations)
            {
                if (applied.TryGetValue(m.Version, out var checksum))
                {
                    if (checksum != m.Checksum)
                    {
                        throw new InvalidOperationException(
                            $"Migration {m.Version:D4}_{m.Name} has changed after it was applied. Add a new migration instead.");
                    }
                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await Exec(conn, m.Sql, ct, tx);
                await using (var record = new NpgsqlCommand(
                    "INSERT INTO schema_migrations (version, name, checksum) VALUES ($1, $2, $3)", conn, tx))
                {
                    record.Parameters.Add(new() { Value = m.Version });
                    record.Parameters.Add(new() { Value = m.Name });
                    record.Parameters.Add(new() { Value = m.Checksum });
                    await record.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                result.Add(m.Version);
            }

            // Types created by a migration (enums, PostGIS) must be visible to Npgsql's type mapping.
            if (result.Count > 0)
            {
                await conn.ReloadTypesAsync(ct);
            }
            return result;
        }
        finally
        {
            await Exec(conn, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    internal static IReadOnlyList<Migration> Load(Assembly assembly)
    {
        const string prefix = "Migrations/";
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var file = Path.GetFileNameWithoutExtension(resource[prefix.Length..]);
            var separator = file.IndexOf('_', StringComparison.Ordinal);
            if (separator != 4 || !int.TryParse(file.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException($"Migration file '{file}' must be named NNNN_name.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // Normalise line endings so checksums do not depend on the checkout.
            var sql = reader.ReadToEnd().ReplaceLineEndings("\n");
            migrations.Add(new Migration(version, file[(separator + 1)..], sql));
        }

        var duplicate = migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Migration version {duplicate.Key:D4} is used more than once.");
        }
        return [.. migrations.OrderBy(m => m.Version)];
    }

    private static async Task<Dictionary<int, string>> AppliedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var applied = new Dictionary<int, string>();
        await using var cmd = new NpgsqlCommand("SELECT version, checksum FROM schema_migrations", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }
        return applied;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

using Cmdb.Catalog;
using Cmdb.DataGen;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Graph;

/// <summary>
/// The operation log and history (#23, ADR-0006, ADR-0013): every write gets an operation in its transaction, a changed
/// row's old version goes to its history table with the operation, a confirmation alone is no version, and the optional
/// hash chain links the operations. Its own network, since it writes.
/// </summary>
public sealed class OperationLogTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_change_keeps_its_old_version_with_the_operation_that_made_it()
    {
        await using var db = await NetworkAsync(51);
        var site = await Scalar<long>(db, "SELECT min(id) FROM site");
        var oldName = await Scalar<string>(db, $"SELECT name FROM site WHERE id = {site}");

        await using (var conn = await db.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            var op = await Scalar<long>(conn, "SELECT cmdb_begin_operation('kim', 'cmdb-web', 'rename', '{\"site\": 1}', 'Nytt namn enligt beslut')");
            await Exec(conn, $"UPDATE site SET name = 'Nytt namn' WHERE id = {site}");
            // The same transaction keeps to the same operation.
            (await Scalar<long>(conn, "SELECT current_setting('cmdb.operation_id')::bigint")).ShouldBe(op);
            await tx.CommitAsync(Ct);
        }

        var history = await Rows(db, $"SELECT h.name, h.operation_id, o.actor, o.command, o.why, h.sys_to <= s.sys_from FROM site_history h JOIN operation o ON o.id = h.operation_id JOIN site s ON s.id = h.id WHERE h.id = {site}");
        history.ShouldHaveSingleItem();
        history[0].ShouldBe([oldName, history[0][1], "kim", "rename", "Nytt namn enligt beslut", true]);

        // A confirmation from an integration is not a version (ADR-0013 §5), and neither is a write that changes nothing.
        await Exec(db, $"UPDATE site SET last_confirmed_at = now() WHERE id = {site}");
        await Exec(db, $"UPDATE site SET name = name WHERE id = {site}");
        (await Scalar<long>(db, $"SELECT count(*) FROM site_history WHERE id = {site}")).ShouldBe(1);

        // A deletion keeps the last version too.
        var connection = await Scalar<long>(db, "SELECT min(id) FROM connection WHERE valid_to IS NULL");
        await Exec(db, $"DELETE FROM connection WHERE id = {connection}");
        (await Scalar<long>(db, $"SELECT count(*) FROM connection_history WHERE id = {connection}")).ShouldBe(1);
    }

    [Fact]
    public async Task A_write_without_a_command_still_gets_an_operation()
    {
        await using var db = await NetworkAsync(52);
        var before = await Scalar<long>(db, "SELECT count(*) FROM operation");
        await Exec(db, "INSERT INTO reservation (resource_kind, resource_id, holder_kind, holder_id, created_by) SELECT 'terminal', min(terminal_id), 'plan', 999, 'test' FROM port");
        var op = (await Rows(db, "SELECT actor, command, payload->>'table', payload->>'op' FROM operation ORDER BY id DESC LIMIT 1"))[0];
        (await Scalar<long>(db, "SELECT count(*) FROM operation")).ShouldBe(before + 1);
        op[1..].ShouldBe(["write", "reservation", "insert"]);
        ((string)op[0]!).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_optional_hash_chain_links_each_operation_to_the_one_before()
    {
        await using var db = await NetworkAsync(53);
        await Exec(db, "UPDATE operation_config SET hash_chain = true");
        for (var i = 0; i < 3; i++)
        {
            await Exec(db, $"SELECT cmdb_begin_operation('kim', NULL, 'test', '{{\"n\": {i}}}', NULL)");
        }
        // Each hash is sha256 over the previous hash and the row, recomputed here from what is stored.
        var broken = await Scalar<long>(db, """
            SELECT count(*) FROM (
                SELECT o.hash, sha256(coalesce(lag(o.hash) OVER (ORDER BY o.id), ''::bytea) || cmdb_operation_text(o)) AS expected,
                       o.prev_hash IS NOT DISTINCT FROM lag(o.hash) OVER (ORDER BY o.id) AS linked
                FROM operation o WHERE o.hash IS NOT NULL) c
            WHERE c.hash <> c.expected OR NOT c.linked
            """);
        broken.ShouldBe(0);
        (await Scalar<long>(db, "SELECT count(*) FROM operation WHERE hash IS NOT NULL")).ShouldBe(3);
        // A changed row breaks the chain where it was changed.
        await Exec(db, "UPDATE operation SET why = 'ändrat i efterhand' WHERE hash IS NOT NULL AND id = (SELECT min(id) FROM operation WHERE hash IS NOT NULL)");
        (await Scalar<long>(db, "SELECT count(*) FROM operation o WHERE o.hash IS NOT NULL AND o.hash <> sha256(coalesce(o.prev_hash, ''::bytea) || cmdb_operation_text(o))")).ShouldBe(1);
    }

    [Fact]
    public async Task A_load_with_its_scenarios_and_demo_plans_keeps_no_history_and_logs_the_generator()
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(58, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, scenarios: true, ct: Ct);

        foreach (var table in Cmdb.Database.OperationLog.HistoryTables)
        {
            (await Scalar<long>(db, $"SELECT count(*) FROM {table}_history")).ShouldBe(0, table);
        }
        (await Scalar<long>(db, "SELECT count(*) FROM operation WHERE actor <> 'datagen'")).ShouldBe(0);
        (await Scalar<long>(db, "SELECT count(*) FROM operation")).ShouldBeGreaterThan(0);
    }

    private async Task<NpgsqlDataSource> NetworkAsync(int seed)
    {
        var db = await factory.NewDatabaseAsync();
        await Loader.LoadAsync(db, NetworkBuilder.Build(seed, Scale.Small, TypeCatalog.Current), reset: false, TextWriter.Null, ct: Ct);
        return db;
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<object?[]>> Rows(NpgsqlDataSource db, string sql)
    {
        var rows = new List<object?[]>();
        await using var cmd = db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }
}

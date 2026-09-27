using Cmdb.Database;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Database;

public sealed class MigratorTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Applies_embedded_migrations_once()
    {
        await using var db = await factory.NewDatabaseAsync();

        var first = await Migrator.MigrateAsync(db, Ct);
        var second = await Migrator.MigrateAsync(db, Ct);

        first.ShouldBe(Migrator.Embedded.Select(m => m.Version));
        second.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_a_migration_that_changed_after_it_was_applied()
    {
        await using var db = await factory.NewDatabaseAsync();
        await Migrator.MigrateAsync(db, [new Migration(1, "t", "CREATE TABLE t (x int)")], Ct);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Migrator.MigrateAsync(db, [new Migration(1, "t", "CREATE TABLE t (x bigint)")], Ct));

        ex.Message.ShouldContain("0001_t has changed");
    }

    [Fact]
    public async Task Rolls_back_a_failing_migration_and_keeps_earlier_ones()
    {
        await using var db = await factory.NewDatabaseAsync();
        Migration[] migrations =
        [
            new(1, "ok", "CREATE TABLE ok (x int)"),
            new(2, "broken", "CREATE TABLE half (x int); SELECT no_such_function()"),
        ];

        await Should.ThrowAsync<PostgresException>(() => Migrator.MigrateAsync(db, migrations, Ct));

        (await Scalar<long>(db, "SELECT count(*) FROM schema_migrations")).ShouldBe(1);
        (await Scalar<bool>(db, "SELECT to_regclass('half') IS NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_runs_apply_each_migration_exactly_once()
    {
        await using var db = await factory.NewDatabaseAsync();

        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Migrator.MigrateAsync(db, Ct)));

        runs.SelectMany(r => r).ShouldBe(Migrator.Embedded.Select(m => m.Version));
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}

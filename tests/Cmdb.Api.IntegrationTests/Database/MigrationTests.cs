using Cmdb.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cmdb.Api.IntegrationTests.Database;

public sealed class MigrationTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Model_has_no_changes_without_a_migration()
    {
        await using var context = CmdbDatabase.CreateContext(factory.Db);

        // Fails when the model was changed but `dotnet ef migrations add` was not run (ADR-0009).
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task Applies_every_migration_once()
    {
        await using var db = await factory.NewDatabaseAsync();
        await using var context = CmdbDatabase.CreateContext(db);

        var first = await CmdbDatabase.MigrateAsync(db, Ct);
        var second = await CmdbDatabase.MigrateAsync(db, Ct);

        first.ShouldBe(context.Database.GetMigrations());
        second.ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_runs_leave_one_consistent_history()
    {
        await using var db = await factory.NewDatabaseAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CmdbDatabase.MigrateAsync(db, Ct)));

        await using var context = CmdbDatabase.CreateContext(db);
        (await context.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(context.Database.GetMigrations());
        await using var cmd = db.CreateCommand("SELECT count(*) FROM site");
        (await cmd.ExecuteScalarAsync(Ct)).ShouldBe(0L);
    }

    [Fact]
    public async Task Lifecycle_labels_keep_their_natural_order()
    {
        await using var cmd = factory.Db.CreateCommand("SELECT enum_range(NULL::lifecycle_state)::text");

        (await cmd.ExecuteScalarAsync(Ct)).ShouldBe("{planned,under_construction,in_service,decommissioning,removed}");
    }
}

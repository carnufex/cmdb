using Cmdb.Database;

namespace Cmdb.Api.Tests.Database;

public sealed class MigrationLoadingTests
{
    [Fact]
    public void Embedded_migrations_are_ordered_and_start_at_one()
    {
        var versions = Migrator.Embedded.Select(m => m.Version).ToList();

        versions.ShouldNotBeEmpty();
        versions.ShouldBe(Enumerable.Range(1, versions.Count));
    }

    [Fact]
    public void Checksum_ignores_line_endings_of_the_checkout()
    {
        var core = Migrator.Embedded[0];

        core.Name.ShouldBe("core");
        core.Sql.ShouldNotContain("\r");
        core.Checksum.Length.ShouldBe(64);
    }
}

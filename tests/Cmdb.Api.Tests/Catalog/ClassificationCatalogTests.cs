using Cmdb.Catalog;

namespace Cmdb.Api.Tests.Catalog;

/// <summary>The classification schemas (#176): the shipped ones load and say which level counts as critical.</summary>
public sealed class ClassificationCatalogTests
{
    [Fact]
    public void The_criticality_schema_has_five_levels_and_level_five_counts_as_critical()
    {
        var schema = ClassificationCatalog.Embedded.Find("criticality").ShouldNotBeNull();

        schema.Levels.Select(l => l.Level).ShouldBe([1, 2, 3, 4, 5]);
        schema.CriticalFrom.ShouldBe(5);
        schema.Level(5)!.Name.ShouldBe("Kritisk");
        schema.AppliesTo.Order().ShouldBe(["cable", "equipment", "service", "site"]);
        schema.Level(6).ShouldBeNull();
    }
}

using Cmdb.Catalog;

namespace Cmdb.Api.Tests.Catalog;

/// <summary>Which source owns which attribute (#215, ADR-0019), as catalog data.</summary>
public sealed class SourcePriorityTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cmdb-priority-").FullName;

    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Earlier.AddDays(1);

    private static readonly SourcePriority Priority = SourcePriority.Parse("""
        [
          { "object": "equipment", "attribute": "placement", "sources": ["nms", "plan"] },
          { "object": "equipment", "attribute": "attributes.*", "sources": ["nms"] },
          { "object": "equipment", "attribute": "attributes.owner", "sources": ["plan"] },
          { "object": "*", "attribute": "lifecycle", "sources": ["plan", "nms"] },
          { "object": "site", "attribute": "*", "sources": ["plan"] }
        ]
        """);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_most_specific_rule_decides()
    {
        Priority.RuleFor("equipment", "attributes.owner")!.Sources.ShouldBe(["plan"]);
        Priority.RuleFor("equipment", "attributes.serialNumber")!.Sources.ShouldBe(["nms"]);
        Priority.RuleFor("equipment", "lifecycle")!.ObjectType.ShouldBe("*");
        Priority.RuleFor("site", "lifecycle")!.ObjectType.ShouldBe("site");
        Priority.RuleFor("equipment", "name").ShouldBeNull();
    }

    [Fact]
    public void Ranks_follow_the_rule_and_a_source_left_out_may_not_write()
    {
        Priority.Rank("equipment", "placement", "nms").ShouldBe(0);
        Priority.Rank("equipment", "placement", "plan").ShouldBe(1);
        Priority.Rank("equipment", "placement", "monitor").ShouldBeNull();
        Priority.Rank("equipment", "name", "monitor").ShouldBe(int.MaxValue);
    }

    [Fact]
    public void The_highest_ranked_source_owns_the_value_and_the_latest_one_without_a_rule()
    {
        Priority.Owner("equipment", "placement", [("plan", Later), ("nms", Earlier)]).ShouldBe("nms");
        Priority.Owner("equipment", "name", [("plan", Earlier), ("nms", Later)]).ShouldBe("nms");
        Priority.Owner("equipment", "attributes.serialNumber", [("plan", Later)]).ShouldBeNull();
    }

    [Fact]
    public void The_embedded_catalog_has_valid_rules_and_a_catalog_without_the_file_has_none()
    {
        SourcePriority.Load(CatalogSource.Embedded).Rules.ShouldNotBeEmpty();
        SourcePriority.Load(CatalogSource.FromDirectory(_folder)).ShouldBeSameAs(SourcePriority.None);
    }

    [Theory]
    [InlineData("""[{ "object": "rack", "attribute": "name", "sources": ["a"] }]""", "unknown object type")]
    [InlineData("""[{ "object": "site", "attribute": "colour", "sources": ["a"] }]""", "unknown attribute")]
    [InlineData("""[{ "object": "circuit", "attribute": "attributes.x", "sources": ["a"] }]""", "unknown attribute")]
    [InlineData("""[{ "object": "site", "attribute": "name", "sources": [] }]""", "at least one source")]
    [InlineData("""[{ "object": "site", "attribute": "name", "sources": ["a", "a"] }]""", "more than once")]
    [InlineData("""[{ "object": "site", "attribute": "name", "sources": ["a"] }, { "object": "site", "attribute": "name", "sources": ["b"] }]""", "more than one rule")]
    [InlineData("""[{ "object": "site", "attribute": "name", "sources": ["a"], "weight": 2 }]""", "weight")]
    public void Invalid_rules_are_rejected(string json, string message)
    {
        Should.Throw<InvalidOperationException>(() => SourcePriority.Parse(json)).Message.ShouldContain(message);
    }
}

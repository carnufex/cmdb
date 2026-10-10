using Cmdb.Catalog;

namespace Cmdb.Api.Tests.Catalog;

/// <summary>How reconciliation links an object from a new source to one cmdb has (#216), as catalog data.</summary>
public sealed class SourceMatchingTests
{
    [Fact]
    public void Rules_are_tried_in_order_per_object_type()
    {
        var matching = SourceMatching.Parse("""
            [
              { "object": "equipment", "keys": ["attributes.serialNumber"] },
              { "object": "site", "keys": ["code"] },
              { "object": "equipment", "keys": ["name", "type"] }
            ]
            """);

        matching.For("equipment").Select(r => string.Join("+", r.Keys)).ShouldBe(["attributes.serialNumber", "name+type"]);
        matching.For("cable").ShouldBeEmpty();
    }

    [Fact]
    public void The_embedded_catalog_has_valid_rules()
    {
        SourceMatching.Load(CatalogSource.Embedded).For("equipment").ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("""[{ "object": "rack", "keys": ["name"] }]""", "unknown object type")]
    [InlineData("""[{ "object": "site", "keys": [] }]""", "at least one key")]
    [InlineData("""[{ "object": "site", "keys": ["position"] }]""", "cannot be matched on")]
    [InlineData("""[{ "object": "equipment", "keys": ["placement"] }]""", "cannot be matched on")]
    [InlineData("""[{ "object": "equipment", "keys": ["attributes.*"] }]""", "cannot be matched on")]
    [InlineData("""[{ "object": "site", "keys": ["code"] }, { "object": "site", "keys": ["code"] }]""", "more than one rule")]
    public void Invalid_rules_are_rejected(string json, string message)
    {
        Should.Throw<InvalidOperationException>(() => SourceMatching.Parse(json)).Message.ShouldContain(message);
    }
}

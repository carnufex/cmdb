using System.Text.Json;
using Cmdb.Catalog;

namespace Cmdb.Api.Tests.Catalog;

public sealed class TypeCatalogTests
{
    private static readonly TypeCatalog Catalog = TypeCatalog.Embedded;

    [Fact]
    public void Embedded_catalog_has_at_least_twenty_models_across_the_main_categories()
    {
        Catalog.Types.Count.ShouldBeGreaterThanOrEqualTo(20);
        var categories = Catalog.Types.Select(t => t.Category).ToHashSet();
        new[] { "switch", "router", "radio", "transmission", "odf", "power" }.ShouldAllBe(c => categories.Contains(c));
    }

    [Fact]
    public void Manufacturers_are_fictional()
    {
        Catalog.Types.ShouldAllBe(t => t.Manufacturer.StartsWith("Acme ", StringComparison.Ordinal));
    }

    [Fact]
    public void Switch_ports_are_numbered_in_zigzag_with_uplinks_after_access_ports()
    {
        var ports = PortExpansion.Expand(Catalog.Find("acme-ax-48")!);

        ports.Count.ShouldBe(52);
        ports[0].ShouldBe(new Port("ge-0/0/1", "RJ45", "access", 1, 0, 0));
        ports[1].ShouldBe(new Port("ge-0/0/2", "RJ45", "access", 2, 1, 0));
        ports[2].ShouldBe(new Port("ge-0/0/3", "RJ45", "access", 3, 0, 1));
        ports[47].ShouldBe(new Port("ge-0/0/48", "RJ45", "access", 48, 1, 23));
        ports[48].ShouldBe(new Port("xe-0/1/1", "SFP+", "uplink", 49, 0, 24));
    }

    [Fact]
    public void Card_ports_carry_the_slot_they_sit_in()
    {
        var ports = PortExpansion.Expand(Catalog.Find("acme-lc-24x")!, slot: "3");

        ports.Select(p => p.Name).Take(2).ShouldBe(["xe-3/0/1", "xe-3/0/2"]);
    }

    [Fact]
    public void Single_ports_without_range_expand_to_one_port()
    {
        var ports = PortExpansion.Expand(Catalog.Find("acme-bat-100")!);

        ports.Select(p => (p.Name, p.Row, p.Column)).ShouldBe([("plus", 0, 0), ("minus", 0, 1)]);
    }

    [Fact]
    public void Attributes_are_validated_against_the_type_schema()
    {
        Catalog.ValidateAttributes("acme-ant-4p", Json("""{ "azimuthDeg": 120, "tiltDeg": -2 }""")).ShouldBeEmpty();

        Catalog.ValidateAttributes("acme-ant-4p", Json("""{ "azimuthDeg": 400 }""")).ShouldNotBeEmpty();
        Catalog.ValidateAttributes("acme-ant-4p", Json("""{ "tiltDeg": 0 }""")).ShouldNotBeEmpty();
        Catalog.ValidateAttributes("acme-ant-4p", Json("""{ "azimuthDeg": 1, "colour": "red" }""")).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("""{ "name": "p{n}", "range": [1, 3], "type": "LC", "at": [0, 0] }, { "name": "q", "type": "LC", "at": [0, 1] }""", "share cell [0, 1]")]
    [InlineData("""{ "name": "p{n}", "range": [1, 5], "type": "LC", "at": [0, 0] }""", "outside the 1x4 panel")]
    [InlineData("""{ "name": "p", "type": "LC", "at": [0, 0] }, { "name": "p", "type": "LC", "at": [0, 1] }""", "used more than once")]
    [InlineData("""{ "name": "p{n}", "type": "LC", "at": [0, 0] }""", "needs a range")]
    [InlineData("""{ "name": "p", "range": [1, 2], "type": "LC", "at": [0, 0] }""", "needs '{n}'")]
    [InlineData("""{ "name": "p", "type": "LC", "at": [0] }""", "'at' must be [row, column]")]
    [InlineData("""{ "name": "p", "type": "LC", "at": [0, 0], "colour": "red" }""", "colour")]
    public void Broken_port_templates_are_rejected(string ports, string expected)
    {
        var ex = Should.Throw<InvalidOperationException>(() => Parse(Type(ports)));

        ex.Message.ShouldContain(expected);
    }

    [Fact]
    public void Unknown_categories_and_misnamed_files_are_rejected()
    {
        var json = Type("""{ "name": "p", "type": "LC", "at": [0, 0] }""").Replace("\"odf\"", "\"toaster\"", StringComparison.Ordinal);

        var ex = Should.Throw<InvalidOperationException>(() => TypeCatalog.Parse([("wrong-name.json", json)]));

        ex.Message.ShouldContain("unknown category 'toaster'");
        ex.Message.ShouldContain("file name must be 't.json'");
    }

    private static TypeCatalog Parse(string json) => TypeCatalog.Parse([("t.json", json)]);

    private static string Type(string ports) => $$"""
        {
          "key": "t", "manufacturer": "Acme Test", "model": "T", "category": "odf",
          "panel": { "rows": 1, "columns": 4 },
          "ports": [ {{ports}} ],
          "attributes": { "type": "object" }
        }
        """;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;
}

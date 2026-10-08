using Cmdb.Catalog;

namespace Cmdb.Api.Tests.Catalog;

/// <summary>Site templates (#26): the shipped ones load, and the checks catch broken ones.</summary>
public sealed class SiteTemplateTests
{
    [Fact]
    public void The_shipped_templates_load_and_hold_equipment_and_cabling()
    {
        var templates = SiteTemplates.Current;

        templates.All.Select(t => t.Key).ShouldContain("radiosite-standard");
        templates.All.Select(t => t.Key).ShouldContain("skap-access");
        templates.All.ShouldAllBe(t => t.Equipment.Count > 0 && t.Connections.Count > 0);
    }

    [Theory]
    [InlineData("""{ "ref": "x", "typeKey": "acme-nope", "name": "{code} X", "rack": "R" }""", "[]", "unknown type")]
    [InlineData("""{ "ref": "x", "typeKey": "acme-lc-24x", "name": "{code} X", "rack": "R" }""", "[]", "is a card")]
    [InlineData("""{ "ref": "x", "typeKey": "acme-ix-8", "name": "X", "rack": "R" }""", "[]", "must contain {code}")]
    [InlineData("""{ "ref": "x", "typeKey": "acme-ix-8", "name": "{code} X", "rack": "R" }""",
        """[{ "from": "x", "fromPort": "ge-0/0/99", "to": "x", "toPort": "ge-0/0/1", "kind": "patch" }]""", "has no port 'ge-0/0/99'")]
    [InlineData("""{ "ref": "x", "typeKey": "acme-ix-8", "name": "{code} X", "rack": "R" }""",
        """[{ "from": "x", "fromPort": "ge-0/0/1", "to": "x", "toPort": "ge-0/0/2", "kind": "patch" }, { "from": "x", "fromPort": "ge-0/0/1", "to": "x", "toPort": "ge-0/0/3", "kind": "patch" }]""",
        "has two patch connections")]
    [InlineData("""{ "ref": "x", "typeKey": "acme-ix-8", "name": "{code} X", "rack": "R" }""",
        """[{ "from": "x", "fromPort": "ge-0/0/1", "to": "y", "toPort": "ge-0/0/2", "kind": "glue" }]""", "unknown equipment 'y'")]
    public void Broken_templates_are_refused_with_the_reason(string equipment, string connections, string message)
    {
        var json = $$"""{ "key": "t", "name": "T", "siteType": "radio", "description": "", "equipment": [{{equipment}}], "connections": {{connections}} }""";

        var error = Should.Throw<InvalidOperationException>(() => SiteTemplates.Parse([("t.json", json)], TypeCatalog.Current));

        error.Message.ShouldContain(message);
    }

    [Fact]
    public void The_key_must_match_the_file_and_the_site_type_must_be_known()
    {
        const string json = """{ "key": "t", "name": "T", "siteType": "castle", "description": "", "equipment": [], "connections": [] }""";

        var error = Should.Throw<InvalidOperationException>(() => SiteTemplates.Parse([("other.json", json)], TypeCatalog.Current));

        error.Message.ShouldContain("must match the file name");
        error.Message.ShouldContain("unknown site type 'castle'");
        error.Message.ShouldContain("needs equipment");
    }
}

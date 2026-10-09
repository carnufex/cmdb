using System.Text.Json;
using Cmdb.Catalog;

namespace Cmdb.Api.Tests.Catalog;

/// <summary>Panel images with ports placed on them (#214).</summary>
public sealed class PanelImageTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Switch_ports_step_through_the_image_in_the_same_zigzag_as_the_grid()
    {
        var ports = PortExpansion.Expand(TypeCatalog.Current.Find("acme-ax-48")!);

        ports[0].Box.ShouldBe(new PortBox("front", 60, 18, 26, 20));
        ports[1].Box.ShouldBe(new PortBox("front", 60, 44, 26, 20));
        ports[2].Box.ShouldBe(new PortBox("front", 90, 18, 26, 20));
        ports[47].Box.ShouldBe(new PortBox("front", 750, 44, 26, 20));
        ports[48].Box.ShouldBe(new PortBox("front", 820, 18, 32, 20));
    }

    [Fact]
    public void Ports_can_sit_on_the_back()
    {
        var type = TypeCatalog.Current.Find("acme-rect-48")!;
        var ports = PortExpansion.Expand(type);

        type.Panel.Images!.Back!.File.ShouldBe("acme-rect-48-back.svg");
        ports.Single(p => p.Name == "ac").Box!.Side.ShouldBe("back");
        ports.Single(p => p.Name == "out8").Box.ShouldBe(new PortBox("back", 740, 66, 50, 40));
        ports.Single(p => p.Name == "mon").Box!.Side.ShouldBe("front");
    }

    [Fact]
    public void Models_without_images_keep_the_grid()
    {
        var type = TypeCatalog.Current.Find("acme-ix-8")!;

        type.Panel.Images.ShouldBeNull();
        PortExpansion.Expand(type).ShouldAllBe(p => p.Box == null);
    }

    [Fact]
    public void Images_are_not_written_for_models_without_them()
    {
        // The stored panel and port template stay as they were for every model without an image.
        var type = TypeCatalog.Current.Find("acme-ix-8")!;

        JsonSerializer.Serialize(type.Panel, Web).ShouldNotContain("images");
        JsonSerializer.Serialize(type.Ports, Web).ShouldNotContain("image");
    }

    [Fact]
    public void A_valid_image_model_loads()
    {
        var catalog = Parse(Type("""{ "name": "{n}", "range": [1, 4], "type": "LC", "at": [0, 0], "image": { "side": "front", "at": [10, 10], "size": [20, 10], "step": [25, 0] } }"""));

        PortExpansion.Expand(catalog.Find("t")!).Select(p => p.Box!.X).ShouldBe([10, 35, 60, 85]);
    }

    [Theory]
    [InlineData("""{ "name": "{n}", "range": [1, 4], "type": "LC", "at": [0, 0], "image": { "side": "front", "at": [10, 10], "size": [20, 10], "step": [30, 0] } }""",
        "port '4' at (100, 10) size 20x10 is outside the 110x40 front image")]
    [InlineData("""{ "name": "{n}", "range": [1, 4], "type": "LC", "at": [0, 0], "image": { "side": "front", "at": [10, 10], "size": [20, 10], "step": [15, 0] } }""",
        "ports 1 and 2 overlap on the front image")]
    [InlineData("""{ "name": "{n}", "range": [1, 4], "type": "LC", "at": [0, 0], "image": { "side": "front", "at": [10, 10], "size": [20, 10] } }""",
        "port '{n}': a range on the image needs 'step'")]
    [InlineData("""{ "name": "{n}", "range": [1, 4], "type": "LC", "at": [0, 0], "image": { "side": "back", "at": [10, 10], "size": [20, 10], "step": [25, 0] } }""",
        "port '1' is on the back, which has no image")]
    [InlineData("""{ "name": "{n}", "range": [1, 4], "type": "LC", "at": [0, 0], "image": { "side": "top", "at": [10, 10], "size": [20, 10], "step": [25, 0] } }""",
        "port '{n}': image side must be 'front' or 'back'")]
    [InlineData("""{ "name": "{n}", "range": [1, 4], "type": "LC", "at": [0, 0], "image": { "side": "front", "at": [10, 10], "size": [0, 10], "step": [25, 0] } }""",
        "port '{n}': image 'size' must be [width, height]")]
    [InlineData("""{ "name": "{n}", "range": [1, 4], "type": "LC", "at": [0, 0] }""",
        "port '1' has no position on the panel images")]
    public void Ports_on_an_image_must_be_inside_it_and_apart(string ports, string expected)
    {
        Should.Throw<InvalidOperationException>(() => Parse(Type(ports))).Message.ShouldContain($"t.json: {expected}");
    }

    [Theory]
    [InlineData("""{ "front": { "file": "missing.svg", "width": 110, "height": 40 } }""", "front image 'missing.svg' is missing from equipment-images/")]
    [InlineData("""{ "front": { "file": "../t.svg", "width": 110, "height": 40 } }""", "front image must be an .svg or .png file name")]
    [InlineData("""{ "front": { "file": "t.gif", "width": 110, "height": 40 } }""", "front image must be an .svg or .png file name")]
    [InlineData("""{ "front": { "file": "t.svg", "width": 0, "height": 40 } }""", "front image needs a width and height")]
    [InlineData("""{ }""", "panel images needs 'front' or 'back'")]
    public void Images_must_be_named_files_in_the_catalog(string images, string expected)
    {
        var ports = """{ "name": "p", "type": "LC", "at": [0, 0], "image": { "side": "front", "at": [0, 0], "size": [10, 10] } }""";

        Should.Throw<InvalidOperationException>(() => Parse(Type(ports, images))).Message.ShouldContain($"t.json: {expected}");
    }

    [Fact]
    public void An_image_position_needs_a_panel_image()
    {
        var ports = """{ "name": "p", "type": "LC", "at": [0, 0], "image": { "side": "front", "at": [0, 0], "size": [10, 10] } }""";

        Should.Throw<InvalidOperationException>(() => Parse(Type(ports, images: null)))
            .Message.ShouldContain("t.json: port 'p' has an image position but the panel has no images");
    }

    private static TypeCatalog Parse(string json) =>
        TypeCatalog.Parse([("t.json", json)], imageExists: f => f is "t.svg" or "t-back.png");

    private static string Type(string ports, string? images = """{ "front": { "file": "t.svg", "width": 110, "height": 40 } }""") => $$"""
        {
          "key": "t", "manufacturer": "Acme Test", "model": "T", "category": "odf",
          "panel": { "rows": 1, "columns": 4{{(images is null ? "" : $", \"images\": {images}")}} },
          "ports": [ {{ports}} ],
          "attributes": { "type": "object" }
        }
        """;
}

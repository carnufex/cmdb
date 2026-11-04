using Cmdb.Catalog;

namespace Cmdb.Api.Tests.Catalog;

public sealed class PlaceCatalogTests
{
    [Theory]
    [InlineData("Gävle", "2180")]
    [InlineData("GÄVLE", "2180")]
    [InlineData("gavle", "2180")]
    [InlineData(" Gävle kommun ", "2180")]
    [InlineData("Stockholms stad", "0180")]
    [InlineData("Göteborgs kommun", "1480")]
    [InlineData("Malmö", "1280")]
    [InlineData("Åre", "2321")]
    [InlineData("Håbo", "0305")]
    [InlineData("Habo", "0643")]
    public void Finds_a_municipality_however_it_is_written(string name, string code) =>
        PlaceCatalog.Find(name)!.Code.ShouldBe(code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ankeborg")]
    [InlineData(" kommun")]
    public void Anything_else_is_no_place(string? name) => PlaceCatalog.Find(name).ShouldBeNull();

    [Fact]
    public void Areas_are_in_the_networks_coordinates()
    {
        // SWEREF 99 TM, not longitude and latitude: Gävle lies about 620 km east of the false origin line and 6 700 km north.
        var wkt = PlaceCatalog.Find("Gävle")!.Wkt;
        wkt.ShouldStartWith("MULTIPOLYGON(((");
        var first = wkt["MULTIPOLYGON(((".Length..].Split(',')[0].Split(' ');
        int.Parse(first[0], System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(550_000, 700_000);
        int.Parse(first[1], System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(6_650_000, 6_800_000);
    }
}

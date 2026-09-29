using System.Net;
using System.Text;
using Cmdb.Api.Features.Map;

namespace Cmdb.Api.IntegrationTests.Features.Map;

public sealed class TilesTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A corner of the grid no other test uses.
    private const double X = 1_650_000;
    private const double Y = 5_650_000;

    [Fact]
    public async Task Serves_sites_and_cables_as_vector_tiles()
    {
        await SeedAsync();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync(Path(8), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.mapbox-vector-tile");
        var tile = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(Ct));
        tile.ShouldContain("sites");
        tile.ShouldContain("cables");
        tile.ShouldContain("TILE-HUB");
        tile.ShouldContain("TILE-RAD");
        tile.ShouldContain("TILE-K24");
    }

    [Fact]
    public async Task Overview_zooms_leave_out_access_sites_and_small_cables()
    {
        await SeedAsync();
        using var client = factory.CreateAuthenticatedClient();

        var tile = Encoding.UTF8.GetString(await client.GetByteArrayAsync(Path(TileGrid.DetailZoom - 1), Ct));

        tile.ShouldContain("TILE-HUB");
        tile.ShouldNotContain("TILE-RAD");
        tile.ShouldNotContain("TILE-K24");
    }

    [Theory]
    [InlineData("/api/tiles/2/4/0")]
    [InlineData("/api/tiles/99/0/0")]
    public async Task Tiles_outside_the_grid_are_not_found(string path)
    {
        using var client = factory.CreateAuthenticatedClient();

        (await client.GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requires_a_token()
    {
        using var client = factory.CreateClient();

        (await client.GetAsync(Path(8), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string Path(int z)
    {
        var size = (double)(TileGrid.MaxX - TileGrid.MinX) / (1 << z);
        var x = (int)((X - TileGrid.MinX) / size);
        var y = (int)((TileGrid.MaxY - Y) / size);
        return $"/api/tiles/{z}/{x}/{y}";
    }

    private async Task SeedAsync()
    {
        await using var cmd = factory.Db.CreateCommand($"""
            INSERT INTO site (code, name, site_type, geom, lifecycle) VALUES
                ('TILE-HUB', 'Tile hub', 'hub', 'SRID=3006;POINT({X} {Y})', 'in_service'),
                ('TILE-RAD', 'Tile radio', 'radio', 'SRID=3006;POINT({X + 500} {Y + 500})', 'planned')
            ON CONFLICT (code) DO NOTHING;
            INSERT INTO cable (cable_type_id, code, a_site_id, b_site_id, geom, lifecycle)
            SELECT ct.id, 'TILE-K24', a.id, b.id, 'SRID=3006;LINESTRING({X} {Y}, {X + 500} {Y + 500})', 'in_service'
            FROM cable_type ct, site a, site b
            WHERE ct.key = 'fiber-24' AND a.code = 'TILE-HUB' AND b.code = 'TILE-RAD'
            ON CONFLICT (code) DO NOTHING;
            """);
        await cmd.ExecuteNonQueryAsync(Ct);
        await factory.RefreshScopesAsync(); // new sites and cables are hidden until visibility is recomputed (#22)
    }
}

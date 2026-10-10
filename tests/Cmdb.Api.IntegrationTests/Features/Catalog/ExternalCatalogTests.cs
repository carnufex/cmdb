using Cmdb.Catalog;
using Cmdb.Database;
using Microsoft.EntityFrameworkCore;

namespace Cmdb.Api.IntegrationTests.Features.Catalog;

/// <summary>A catalog read from a folder (<c>CMDB_CATALOG_PATH</c>, #207) is validated and synced like the embedded one.</summary>
public sealed class ExternalCatalogTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "TestCatalog");

    [Fact]
    public async Task Syncs_the_types_from_an_external_folder_into_the_database()
    {
        await using var db = await factory.NewDatabaseAsync();
        await CmdbDatabase.MigrateAsync(db, Ct);
        var source = CatalogSource.FromPath(Folder);
        var catalog = TypeCatalog.Load(source);
        await using var context = CmdbDatabase.CreateContext(db);

        (await CatalogSync.SyncAsync(context, catalog, Ct)).ShouldBeGreaterThan(0);

        var types = await context.EquipmentTypes.Where(t => t.Key.StartsWith("globex-")).OrderBy(t => t.Key).Select(t => t.Key).ToListAsync(Ct);
        types.ShouldBe(["globex-odf-8", "globex-sw-4"]);
        (await context.CableTypes.SingleAsync(t => t.Key == "globex-fiber-8", Ct)).ConductorCount.ShouldBe(8);
        (await context.DuctTypes.SingleAsync(t => t.Key == "globex-md-4x10", Ct)).SubductCount.ShouldBe(4);
        // A second sync of the same folder writes nothing.
        (await CatalogSync.SyncAsync(context, catalog, Ct)).ShouldBe(0);
    }

    [Fact]
    public void Templates_and_classifications_come_from_the_same_folder()
    {
        var source = CatalogSource.FromPath(Folder);

        SiteTemplates.Load(source, TypeCatalog.Load(source)).All.Select(t => t.Key).ShouldBe(["globex-skap"]);
        ClassificationCatalog.Load(source).Find("criticality")!.CriticalFrom.ShouldBe(3);
    }

    [Fact]
    public void Panel_images_come_from_the_same_folder()
    {
        var source = CatalogSource.FromPath(Folder);
        var type = TypeCatalog.Load(source).Find("globex-sw-4")!;

        type.Panel.Images!.Front!.File.ShouldBe("globex-sw-4-front.png");
        source.Image("globex-sw-4-front.png")!.Take(4).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
        CatalogSource.Embedded.Image("globex-sw-4-front.png").ShouldBeNull();
        PortExpansion.Expand(type).Select(p => p.Box!.X).ShouldBe([20, 60, 100, 140]);
    }

    [Fact]
    public void Site_types_and_categories_with_other_names_keep_their_roles()
    {
        var catalog = TypeCatalog.Load(CatalogSource.FromPath(Folder));

        catalog.SiteTypesWith(CatalogRoles.Hub, CatalogRoles.Aggregation).ShouldBe(["karna", "nod"]);
        catalog.SiteTypeHas("brunn", CatalogRoles.SplicePoint).ShouldBeTrue();
        catalog.SiteTypeHas("karna", CatalogRoles.Access).ShouldBeFalse();
        catalog.TypeHas("globex-odf-8", CatalogRoles.Termination).ShouldBeTrue();
        catalog.CategoriesWith(CatalogRoles.Power).ShouldBe(["batteri"]);
        catalog.FindSiteType("hub").ShouldBeNull();
        Cmdb.Api.Features.Query.QueryFieldsEndpoint.SiteTypeFields(catalog).Select(t => t.Name).ShouldBe(["Kärnnod", "Nod", "Kundskåp", "Skarvbrunn"]);
    }
}

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
}

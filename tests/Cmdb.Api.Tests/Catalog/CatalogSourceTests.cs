using Cmdb.Catalog;

namespace Cmdb.Api.Tests.Catalog;

public sealed class CatalogSourceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("cmdb-catalog-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Without_a_path_the_embedded_catalog_is_used()
    {
        CatalogSource.FromPath(null).ShouldBeSameAs(CatalogSource.Embedded);
        CatalogSource.FromPath(" ").ShouldBeSameAs(CatalogSource.Embedded);
    }

    [Fact]
    public void A_folder_with_the_embedded_files_gives_the_same_catalog()
    {
        CopyEmbedded();
        var source = CatalogSource.FromPath(_folder);

        var types = TypeCatalog.Load(source);
        types.Types.Select(t => t.Key).Order().ShouldBe(TypeCatalog.Load(CatalogSource.Embedded).Types.Select(t => t.Key).Order());
        types.CableTypes.Count.ShouldBe(TypeCatalog.Load(CatalogSource.Embedded).CableTypes.Count);
        SiteTemplates.Load(source, types).All.Count.ShouldBe(SiteTemplates.Load(CatalogSource.Embedded, types).All.Count);
        ClassificationCatalog.Load(source).Find("criticality").ShouldNotBeNull();
    }

    [Fact]
    public void Service_types_are_optional_in_a_folder()
    {
        CopyEmbedded();
        TypeCatalog.Load(CatalogSource.FromPath(_folder)).ServiceTypes.ShouldBeEmpty();

        File.WriteAllText(Path.Combine(_folder, "service-types.json"), """[{ "key": "fiber-access", "name": "Fiberaccess" }]""");
        TypeCatalog.Load(CatalogSource.FromPath(_folder)).ServiceTypes.Single().Key.ShouldBe("fiber-access");
    }

    [Fact]
    public void A_missing_folder_is_an_error_naming_the_variable()
    {
        var missing = Path.Combine(_folder, "nope");

        Should.Throw<InvalidOperationException>(() => CatalogSource.FromPath(missing)).Message.ShouldContain("CMDB_CATALOG_PATH");
    }

    [Fact]
    public void Errors_name_the_folder_the_file_and_the_rule()
    {
        CopyEmbedded();
        File.WriteAllText(Path.Combine(_folder, "equipment-types", "acme-ix-8.json"),
            File.ReadAllText(Path.Combine(_folder, "equipment-types", "acme-ix-8.json")).Replace("\"switch\"", "\"toaster\"", StringComparison.Ordinal));

        var message = Should.Throw<InvalidOperationException>(() => TypeCatalog.Load(CatalogSource.FromPath(_folder))).Message;

        message.ShouldContain("acme-ix-8.json: unknown category 'toaster'");
        message.ShouldContain(_folder);
    }

    [Fact]
    public void Cable_types_and_criticality_are_required()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "equipment-types"));

        Should.Throw<InvalidOperationException>(() => TypeCatalog.Load(CatalogSource.FromPath(_folder))).Message.ShouldContain("cable-types.json: missing");
        Should.Throw<InvalidOperationException>(() => ClassificationCatalog.Load(CatalogSource.FromPath(_folder))).Message.ShouldContain("criticality");
    }

    private void CopyEmbedded()
    {
        foreach (var folder in new[] { "equipment-types", "classifications", "site-templates" })
        {
            Directory.CreateDirectory(Path.Combine(_folder, folder));
            foreach (var (file, json) in CatalogSource.Embedded.Files(folder))
            {
                File.WriteAllText(Path.Combine(_folder, folder, file), json);
            }
        }
        foreach (var file in new[] { "cable-types.json", "site-types.json", "equipment-categories.json" })
        {
            File.WriteAllText(Path.Combine(_folder, file), CatalogSource.Embedded.Read(file));
        }
    }
}

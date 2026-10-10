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
    public void Panel_images_come_from_the_folder_and_must_be_there()
    {
        CopyEmbedded();
        var source = CatalogSource.FromPath(_folder);
        source.Image("acme-odf-96-front.svg").ShouldBe(CatalogSource.Embedded.Image("acme-odf-96-front.svg"));

        File.Delete(Path.Combine(_folder, CatalogSource.ImageFolder, "acme-pp-24-front.svg"));

        Should.Throw<InvalidOperationException>(() => TypeCatalog.Load(CatalogSource.FromPath(_folder)))
            .Message.ShouldContain("acme-pp-24.json: front image 'acme-pp-24-front.svg' is missing from equipment-images/");
    }

    [Theory]
    [InlineData("../cable-types.json")]
    [InlineData("..%2Fcable-types.json")]
    [InlineData("equipment-types/acme-ix-8.json")]
    [InlineData("/etc/passwd")]
    [InlineData("acme-odf-96-front.SVG")]
    [InlineData("nope.svg")]
    public void Only_plain_image_names_in_the_image_folder_are_read(string file)
    {
        CopyEmbedded();
        File.WriteAllText(Path.Combine(_folder, "secret.svg"), "<svg/>");

        CatalogSource.FromPath(_folder).Image(file).ShouldBeNull();
        CatalogSource.Embedded.Image(file).ShouldBeNull();
    }

    [Fact]
    public void Every_embedded_image_is_used_by_a_model()
    {
        var used = TypeCatalog.Load(CatalogSource.Embedded).Types
            .SelectMany(t => new[] { t.Panel.Images?.Front, t.Panel.Images?.Back })
            .OfType<PanelImage>().Select(i => i.File).ToHashSet();

        EmbeddedImages.ShouldNotBeEmpty();
        EmbeddedImages.ShouldAllBe(f => used.Contains(f));
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
    public void Duct_types_are_optional_and_checked_like_the_other_types()
    {
        CopyEmbedded();
        TypeCatalog.Load(CatalogSource.FromPath(_folder)).DuctTypes.ShouldBeEmpty();

        var embedded = TypeCatalog.Load(CatalogSource.Embedded);
        embedded.DuctTypes.Select(t => t.Key).Order().ShouldBe(["acme-md-24x7", "acme-md-7x16", "acme-sd-40"]);
        embedded.FindDuct("acme-md-7x16")!.Subducts.ShouldBe(new SubductTemplate(7, 12, "IEC 60304"));
        embedded.FindSiteType("manhole")!.Name.ShouldBe("Brunn");

        var ducts = Directory.CreateDirectory(Path.Combine(_folder, "duct-types")).FullName;
        File.WriteAllText(Path.Combine(ducts, "globex-md-4x10.json"), """
            { "key": "globex-md-4x10", "name": "Multidukt 4×10", "manufacturer": "Globex", "model": "MD-4x10", "outerDiameterMm": 30,
              "subducts": { "count": 4, "innerDiameterMm": 8 } }
            """);
        TypeCatalog.Load(CatalogSource.FromPath(_folder)).FindDuct("globex-md-4x10")!.Subducts.Count.ShouldBe(4);

        File.WriteAllText(Path.Combine(ducts, "globex-bad.json"), """
            { "key": "globex-other", "name": "Fel", "manufacturer": "Globex", "model": "X", "outerDiameterMm": 10,
              "subducts": { "count": 0, "innerDiameterMm": 12 } }
            """);
        var message = Should.Throw<InvalidOperationException>(() => TypeCatalog.Load(CatalogSource.FromPath(_folder))).Message;
        message.ShouldContain("duct-types/globex-bad.json: file name must be 'globex-other.json'");
        message.ShouldContain("subducts.count is 1–1000");
        message.ShouldContain("less than the outer diameter");
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
        Directory.CreateDirectory(Path.Combine(_folder, CatalogSource.ImageFolder));
        foreach (var file in EmbeddedImages)
        {
            File.WriteAllBytes(Path.Combine(_folder, CatalogSource.ImageFolder, file), CatalogSource.Embedded.Image(file)!);
        }
    }

    private static IEnumerable<string> EmbeddedImages =>
        typeof(CatalogSource).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(CatalogSource.ImageFolder + "/", StringComparison.Ordinal))
            .Select(n => n[(CatalogSource.ImageFolder.Length + 1)..]);
}

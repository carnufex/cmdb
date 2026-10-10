using Cmdb.Catalog;
using Cmdb.DataGen.CatalogGeneration;

namespace Cmdb.Api.Tests.DataGen;

/// <summary>Catalog entries generated from a synthetic export of equipment and ports (#209).</summary>
public sealed class CatalogGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cmdb-catgen-{Guid.NewGuid():N}");

    private string Export => Path.Combine(_root, "export");

    private string Output => Path.Combine(_root, "catalog");

    public CatalogGeneratorTests()
    {
        Directory.CreateDirectory(Export);
        Directory.CreateDirectory(Output);
        // A catalog folder with the base files and no equipment yet.
        File.WriteAllText(Path.Combine(Output, "cable-types.json"), CatalogSource.Embedded.Read("cable-types.json"));
        File.WriteAllText(Path.Combine(Output, "site-types.json"), CatalogSource.Embedded.Read("site-types.json"));
        File.WriteAllText(Path.Combine(Output, "equipment-categories.json"), """
            [
              { "key": "switch", "name": "Switch", "roles": [] },
              { "key": "modul", "name": "Modul", "roles": ["card"] }
            ]
            """);
        Write("equipment.csv", """
            id;model;manufacturer;category;rackUnits;parent;slot
            E1;SW-24G;Initech;switch;1;;
            E2;SW-24G;Initech;switch;1;;
            E8;SW-24G;Initech;switch;1;;
            E3;CH-6;Initech;;6;;
            E4;LC-4X;Initech;;;E3;3
            E5;LC-4X;Initech;;;E3;5
            E6;PP-60;Initech;;2;;
            E7;§§§;;;;;
            """);
        var ports = new List<string> { "equipment;name;type;group" };
        foreach (var e in new[] { "E1", "E2", "E8" })
        {
            ports.AddRange(Enumerable.Range(1, 24).Select(n => $"{e};ge-0/0/{n};RJ45;access"));
            ports.Add($"{e};xe-0/1/1;SFP+;uplink");
            ports.Add($"{e};mgmt;RJ45;management");
        }
        // One unit says the uplink is SFP28: the most common type wins, with a warning.
        ports.Add("E2;xe-0/1/2;SFP28;uplink");
        ports.Add("E1;xe-0/1/2;SFP+;uplink");
        ports.Add("E8;xe-0/1/2;SFP+;uplink");
        ports.Add("E3;xe-0/1/2;SFP+;uplink");
        ports.Add("E3;xe-0/1/3;SFP+;uplink");
        ports.AddRange(new[] { ("E4", 3), ("E5", 5) }.SelectMany(c => Enumerable.Range(1, 4).Select(n => $"{c.Item1};et-{c.Item2}/0/{n};QSFP28;line")));
        ports.AddRange(Enumerable.Range(1, 60).Select(n => $"E6;{n};RJ45;"));
        ports.Add("E6;P01;;");
        ports.Add("E6;P02;;");
        Write("ports.csv", string.Join('\n', ports));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Generates_a_type_per_model_that_the_catalog_loads()
    {
        var report = CatalogGenerator.Generate(Export, Output, force: false);

        report.Generated.Order().ShouldBe(["initech-ch-6", "initech-lc-4x", "initech-pp-60", "initech-sw-24g"]);
        report.Validation.ShouldBe("");
        report.Skipped.ShouldContain(s => s.Model == "§§§");
        report.AddedCategories.ShouldBe(["ovrigt"]);
        report.Warnings.ShouldContain(w => w.Model == "Initech SW-24G" && w.Warning.Contains("olika typ"));
        report.Warnings.ShouldContain(w => w.Model == "Initech PP-60" && w.Warning.Contains("2 portar"));

        var catalog = TypeCatalog.Load(CatalogSource.FromDirectory(Output));
        // Numbered ports become runs; the rest stay single, in order of first appearance.
        var sw = catalog.Find("initech-sw-24g")!;
        sw.Ports.Select(p => (p.Name, p.Range?[0], p.Range?[1])).ShouldBe(
            [("ge-0/0/{n}", 1, 24), ("xe-0/1/{n}", 1, 2), ("mgmt", null, null)]);
        sw.Ports[1].Type.ShouldBe("SFP+");
        (sw.Category, sw.RackUnits).ShouldBe(("switch", 1));
        PortExpansion.Expand(sw).Count.ShouldBe(27);

        // A run wider than the panel wraps onto the next row; zero-padded names are kept as they are.
        var panel = catalog.Find("initech-pp-60")!;
        panel.Panel.Columns.ShouldBe(CatalogGenerator.MaxColumns);
        PortExpansion.Expand(panel).Select(p => p.Name).ShouldContain("P01");
        PortExpansion.Expand(panel).Count.ShouldBe(62);

        // Cards: the slot number in the port names becomes {slot}, the category the folder's card category, and the chassis gets the slots.
        var card = catalog.Find("initech-lc-4x")!;
        card.Category.ShouldBe("modul");
        card.Ports.Single().Name.ShouldBe("et-{slot}/0/{n}");
        var chassis = catalog.Find("initech-ch-6")!;
        chassis.SlotList.Select(s => (s.Name, string.Join(',', s.Accepts))).ShouldBe([("3", "modul"), ("5", "modul")]);
        chassis.Ports.Select(p => p.Name).ShouldBe(["xe-0/1/{n}"]);
    }

    [Fact]
    public void Leaves_existing_files_unless_forced()
    {
        CatalogGenerator.Generate(Export, Output, force: false);
        var again = CatalogGenerator.Generate(Export, Output, force: false);

        again.Generated.ShouldBeEmpty();
        again.Skipped.Count(s => s.Reason.Contains("--force")).ShouldBe(4);
        CatalogGenerator.Generate(Export, Output, force: true).Generated.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("Initech Optical FR-12", "initech-optical-fr-12")]
    [InlineData("Ångström Nät 4/8", "angstrom-nat-4-8")]
    [InlineData("--X--", "x")]
    public void Keys_are_lower_case_letters_digits_and_dashes(string name, string key) => CatalogGenerator.Slug(name).ShouldBe(key);

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(Export, file), text);
}

namespace Cmdb.DataGen.CatalogGeneration;

/// <summary><c>catalog generate --from &lt;export&gt; --out &lt;folder&gt; [--force]</c>: catalog entries from an export (#209).</summary>
internal static class CatalogGenerationCli
{
    private const string Usage = "Usage: catalog generate --from <export folder> --out <catalog folder> [--force]";

    public static int Run(string[] args)
    {
        string? from = null, output = null;
        var force = false;
        if (args.Length < 2 || args[1] != "generate")
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" when i + 1 < args.Length:
                    from = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'. {Usage}");
                    return 1;
            }
        }
        if (from is null || output is null)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var report = CatalogGenerator.Generate(from, output, force);
        Console.WriteLine($"Genererade {report.Generated.Count} utrustningstyper i {Path.Combine(output, "equipment-types")}");
        if (report.AddedCategories.Count > 0)
        {
            Console.WriteLine($"Nya kategorier i equipment-categories.json, utan roller: {string.Join(", ", report.AddedCategories)}");
        }
        foreach (var (model, warning) in report.Warnings)
        {
            Console.WriteLine($"  varning  {model}: {warning}");
        }
        foreach (var (model, reason) in report.Skipped)
        {
            Console.WriteLine($"  hoppade över  {model}: {reason}");
        }
        Console.WriteLine(report.Validation switch
        {
            null => "Mappen saknar cable-types.json eller site-types.json; varje typ är kontrollerad för sig.",
            "" => "Katalogen i mappen laddas utan fel.",
            var errors => $"Katalogen i mappen laddas inte:{Environment.NewLine}{errors}",
        });
        return report.Validation is null or "" ? 0 : 2;
    }
}

using System.Globalization;
using Cmdb.Catalog;
using Cmdb.Database;
using Npgsql;

namespace Cmdb.DataGen.Exchange;

/// <summary>
/// <c>import --from &lt;folder&gt; --source &lt;system&gt; [--dry-run]</c> imports a network in the exchange format into
/// production (#210). <c>export --to &lt;folder&gt; --scale small --seed 1</c> writes a generated network in the same format,
/// for examples and for measuring the import. The catalog is <c>CMDB_CATALOG_PATH</c>'s, or the embedded one.
/// </summary>
internal static class ExchangeCli
{
    private const string Usage = "Usage: import --from <folder> --source <system> [--dry-run] [--connection CS] | export --to <folder> [--scale S] [--seed N]";

    public static async Task<int> RunAsync(string[] args)
    {
        string? folder = null, source = null;
        var dryRun = false;
        var scale = Scale.Small;
        var seed = 1;
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Cmdb") ?? "Host=127.0.0.1;Port=15432;Database=cmdb;Username=cmdb";
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" or "--to" when i + 1 < args.Length:
                    folder = args[++i];
                    break;
                case "--source" when i + 1 < args.Length:
                    source = args[++i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--scale" when i + 1 < args.Length:
                    scale = Scale.Parse(args[++i]);
                    break;
                case "--seed" when i + 1 < args.Length:
                    seed = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'. {Usage}");
                    return 1;
            }
        }
        if (folder is null || (args[0] == "import" && string.IsNullOrWhiteSpace(source)))
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (args[0] == "export")
        {
            var network = NetworkBuilder.Build(seed, scale, TypeCatalog.Current);
            ExchangeExport.Write(network, folder);
            Console.WriteLine($"Wrote the {scale.Name} network (seed {seed}) to {folder}");
            return 0;
        }

        var builder = new NpgsqlConnectionStringBuilder(connection) { CommandTimeout = 0 };
        await using var db = CmdbDatabase.CreateDataSource(builder.ConnectionString);
        var result = await NetworkImport.RunAsync(db, folder, source!, TypeCatalog.Current, dryRun, Console.Out);
        foreach (var error in result.Errors.Take(1000))
        {
            Console.Error.WriteLine(error);
        }
        if (result.Errors.Count > 1000)
        {
            Console.Error.WriteLine($"... and {result.Errors.Count - 1000} more");
        }
        foreach (var c in result.Counts)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {c.Kind,-22} {c.Created,9:N0} new {c.Updated,9:N0} changed {c.Unchanged,9:N0} unchanged {c.Missing,9:N0} only in the database"));
        }
        Console.WriteLine(result.Written ? "Imported." : result.Errors.Count > 0 ? $"{result.Errors.Count} problem(s); nothing was written." : "Dry run; nothing was written.");
        return result.Errors.Count > 0 ? 2 : 0;
    }
}
